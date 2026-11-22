using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using FileKit.Contracts.Effects;
using FileKit.Host.Interop;
using Microsoft.Win32;

namespace FileKit.Host.Services;

/// <summary>
/// 特效材质应用器：把材质栈顶的 <see cref="BackdropType"/> 落到窗口；
/// 栈空（effective 为 null）时恢复 M1 默认材质/纯色降级。
/// 本类不修改 M1 的 <see cref="WindowMaterialService"/>，只在需要时复用它。
/// 任何 DWM/环境失败都只降级为纯色，不抛异常。
/// </summary>
public sealed class EffectMaterialController : IDisposable
{
    private const string PersonalizeKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private const string TransparencyValueName = "EnableTransparency";

    private readonly Window _window;
    private readonly IntPtr _hwnd;
    private readonly WindowMaterialService _materialService = new();
    private readonly Panel? _contentRoot;
    private readonly Brush? _fallbackBrush;
    private bool _disposed;

    public EffectMaterialController(Window window, IntPtr hwnd)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _hwnd = hwnd;
        _contentRoot = window.Content as Panel;
        _fallbackBrush = window.TryFindResource("SurfaceFallbackBrush") as Brush;
    }

    /// <summary>
    /// 应用当前生效材质。<paramref name="effective"/> 为 null 表示材质栈为空，恢复 M1 默认。
    /// </summary>
    public void Apply(BackdropType? effective)
    {
        if (_disposed)
        {
            return;
        }

        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (effective is null)
            {
                if (_materialService.TryApply(_hwnd))
                {
                    SetTransparentVisuals();
                }
                else
                {
                    // M1 默认材质不可用时，先清掉特效可能残留的原生背景，再回退纯色。
                    ClearBackdrop();
                    SetSolidVisuals();
                }

                return;
            }

            if (effective.Value == BackdropType.None)
            {
                ClearBackdrop();
                SetSolidVisuals();
                return;
            }

            if (TryApplyExplicit(_hwnd, effective.Value))
            {
                SetTransparentVisuals();
            }
            else
            {
                ClearBackdrop();
                SetSolidVisuals();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileKit] Effect material apply threw: {ex.GetType().Name}: {ex.Message}");

            try
            {
                SetSolidVisuals();
            }
            catch
            {
                // 纯色降级失败不继续抛。
            }
        }
    }

    public void Dispose()
    {
        // 栈恢复由 WindowEffectHost 负责（清栈后调用 Apply(null)）；这里只标记释放。
        _disposed = true;
    }

    private static bool TryApplyExplicit(IntPtr hwnd, BackdropType type)
    {
        if (!AreTransparencyEffectsEnabled())
        {
            Debug.WriteLine("[FileKit] Effect material skipped: transparency effects are disabled.");
            return false;
        }

        int compositionResult = DwmApi.DwmIsCompositionEnabled(out int compositionEnabled);
        if (compositionResult != 0 || compositionEnabled == 0)
        {
            Debug.WriteLine(
                $"[FileKit] Effect material skipped: DWM composition is unavailable (HRESULT=0x{compositionResult:X8}).");
            return false;
        }

        int build = Environment.OSVersion.Version.Build;
        if (build < 22000)
        {
            Debug.WriteLine($"[FileKit] Effect material skipped: Windows build {build} does not support backdrops.");
            return false;
        }

        int result;
        if (build >= 22621)
        {
            int backdrop = type == BackdropType.Mica
                ? DwmApi.DwmsbtMainWindow
                : DwmApi.DwmsbtTransientWindow;

            result = DwmApi.DwmSetWindowAttribute(
                hwnd,
                DwmApi.DwmwaSystemBackdropType,
                ref backdrop,
                sizeof(int));
        }
        else
        {
            // Windows 11 21H2 (build 22000) 只有旧版 Mica 属性；Acrylic 走纯色降级。
            if (type != BackdropType.Mica)
            {
                Debug.WriteLine($"[FileKit] Effect material skipped: {type} is unsupported on build {build}.");
                return false;
            }

            int legacyMica = 1;
            result = DwmApi.DwmSetWindowAttribute(
                hwnd,
                DwmApi.DwmwaMicaEffect,
                ref legacyMica,
                sizeof(int));
        }

        if (result != 0)
        {
            Debug.WriteLine($"[FileKit] Effect backdrop {type} failed (HRESULT=0x{result:X8}).");
            return false;
        }

        var margins = new DwmApi.Margins
        {
            Left = -1,
            Right = -1,
            Top = -1,
            Bottom = -1,
        };

        int extendResult = DwmApi.DwmExtendFrameIntoClientArea(hwnd, ref margins);
        if (extendResult != 0)
        {
            Debug.WriteLine(
                $"[FileKit] DwmExtendFrameIntoClientArea failed (HRESULT=0x{extendResult:X8}); falling back to solid.");
            ClearBackdrop(hwnd, build);
            return false;
        }

        return true;
    }

    private void ClearBackdrop()
    {
        ClearBackdrop(_hwnd, Environment.OSVersion.Version.Build);
    }

    private static void ClearBackdrop(IntPtr hwnd, int build)
    {
        try
        {
            if (build >= 22621)
            {
                int none = DwmApi.DwmsbtNone;
                int result = DwmApi.DwmSetWindowAttribute(
                    hwnd,
                    DwmApi.DwmwaSystemBackdropType,
                    ref none,
                    sizeof(int));

                if (result != 0)
                {
                    Debug.WriteLine($"[FileKit] Could not clear the backdrop (HRESULT=0x{result:X8}).");
                }
            }
            else if (build >= 22000)
            {
                int disabled = 0;
                int result = DwmApi.DwmSetWindowAttribute(
                    hwnd,
                    DwmApi.DwmwaMicaEffect,
                    ref disabled,
                    sizeof(int));

                if (result != 0)
                {
                    Debug.WriteLine($"[FileKit] Could not clear the legacy Mica effect (HRESULT=0x{result:X8}).");
                }
            }

            // 恢复客户区边框，避免纯色内容被 -1 边距影响。
            var margins = new DwmApi.Margins();
            DwmApi.DwmExtendFrameIntoClientArea(hwnd, ref margins);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileKit] Clearing the backdrop threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool AreTransparencyEffectsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                PersonalizeKeyPath,
                writable: false);

            object? value = key?.GetValue(TransparencyValueName);
            if (value is null)
            {
                return true;
            }

            return Convert.ToInt32(value) != 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[FileKit] Could not read the transparency setting ({ex.GetType().Name}); using the solid background.");
            return false;
        }
    }

    private void SetTransparentVisuals()
    {
        try
        {
            // WPF 合成表面默认黑色；透明后才能露出 DWM 原生材质。
            if (HwndSource.FromHwnd(_hwnd)?.CompositionTarget is { } compositionTarget)
            {
                compositionTarget.BackgroundColor = Colors.Transparent;
            }

            if (_contentRoot is not null)
            {
                _contentRoot.Background = Brushes.Transparent;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileKit] Transparent visuals failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void SetSolidVisuals()
    {
        try
        {
            if (HwndSource.FromHwnd(_hwnd)?.CompositionTarget is { } compositionTarget)
            {
                compositionTarget.BackgroundColor = Colors.Black;
            }

            if (_contentRoot is not null)
            {
                _contentRoot.Background = _fallbackBrush
                    ?? new SolidColorBrush(Color.FromRgb(0xE9, 0xED, 0xF2));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileKit] Solid visuals failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
