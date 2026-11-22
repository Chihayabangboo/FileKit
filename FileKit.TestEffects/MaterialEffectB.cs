using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FileKit.Contracts.Effects;

namespace FileKit.TestEffects;

/// <summary>
/// 样例：覆盖窗口材质为 Acrylic 并显示标签“特效 B（Acrylic）”。
/// 用于验收材质栈：A/B 同时安装后卸载 B，材质栈应回退到 A（Mica）。
/// </summary>
public sealed class MaterialEffectB : IFileKitEffect
{
    private IEffectHost? _host;
    private FrameworkElement? _overlay;
    private bool _backdropPushed;
    private bool _disposed;

    public string EffectId => "filekit.dev.material-b";

    public EffectTarget Target => EffectTarget.Window;

    public void Attach(IEffectHost host)
    {
        if (host is null || _disposed || _overlay is not null)
        {
            return;
        }

        try
        {
            host.PushBackdrop(EffectId, BackdropType.Acrylic);
            _backdropPushed = true;

            Border overlay = SampleOverlay.Create(
                "特效 B（Acrylic）",
                Color.FromArgb(0xB3, 0x5A, 0x2D, 0x91),
                HorizontalAlignment.Left,
                VerticalAlignment.Top,
                new Thickness(24, 64, 0, 0));

            host.RegisterOverlay(overlay, 112);
            _host = host;
            _overlay = overlay;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileKit.TestEffects] MaterialEffectB.Attach failed: {ex.GetType().Name}: {ex.Message}");
            PopBackdropQuietly(host);
            _host = null;
            _overlay = null;
        }
    }

    public void Detach()
    {
        FrameworkElement? overlay = _overlay;
        IEffectHost? host = _host;
        _overlay = null;
        _host = null;

        if (host is null)
        {
            _backdropPushed = false;
            return;
        }

        PopBackdropQuietly(host);

        if (overlay is not null)
        {
            try
            {
                host.UnregisterOverlay(overlay);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FileKit.TestEffects] MaterialEffectB.Detach failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            Detach();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileKit.TestEffects] MaterialEffectB.Dispose failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void PopBackdropQuietly(IEffectHost? host)
    {
        if (!_backdropPushed || host is null)
        {
            _backdropPushed = false;
            return;
        }

        try
        {
            host.PopBackdrop(EffectId);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileKit.TestEffects] MaterialEffectB.PopBackdrop failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _backdropPushed = false;
        }
    }
}
