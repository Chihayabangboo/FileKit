using System.Diagnostics;
using FileKit.Host.Interop;
using Microsoft.Win32;

namespace FileKit.Host.Services;

/// <summary>
/// Attempts to enable a native Windows 11 backdrop and silently falls back to a
/// solid surface when any prerequisite or DWM call is unavailable.
/// </summary>
internal sealed class WindowMaterialService
{
    private const string PersonalizeKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private const string TransparencyValueName = "EnableTransparency";

    internal bool TryApply(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero)
            {
                Debug.WriteLine("[FileKit] Material skipped: the window handle is not available.");
                return false;
            }

            if (!AreTransparencyEffectsEnabled())
            {
                Debug.WriteLine("[FileKit] Material skipped: transparency effects are disabled.");
                return false;
            }

            int compositionResult = DwmApi.DwmIsCompositionEnabled(out int compositionEnabled);
            if (compositionResult != 0 || compositionEnabled == 0)
            {
                Debug.WriteLine(
                    $"[FileKit] Material skipped: DWM composition is unavailable (HRESULT=0x{compositionResult:X8}).");
                return false;
            }

            int build = Environment.OSVersion.Version.Build;
            if (build < 22000)
            {
                Debug.WriteLine($"[FileKit] Material skipped: Windows build {build} does not support Mica.");
                return false;
            }

            if (!TryApplyBackdrop(hwnd, build))
            {
                Debug.WriteLine("[FileKit] Material unavailable; using the solid background.");
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
                    $"[FileKit] DwmExtendFrameIntoClientArea failed (HRESULT=0x{extendResult:X8}); using the solid background.");
                DisableBackdrop(hwnd, build);
                return false;
            }

            Debug.WriteLine($"[FileKit] Native backdrop enabled on Windows build {build}.");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileKit] Material setup threw {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static bool TryApplyBackdrop(IntPtr hwnd, int build)
    {
        if (build >= 22621)
        {
            int mica = DwmApi.DwmsbtMainWindow;
            int micaResult = DwmApi.DwmSetWindowAttribute(
                hwnd,
                DwmApi.DwmwaSystemBackdropType,
                ref mica,
                sizeof(int));

            if (micaResult == 0)
            {
                return true;
            }

            Debug.WriteLine(
                $"[FileKit] Mica backdrop failed (HRESULT=0x{micaResult:X8}); trying Acrylic.");

            int acrylic = DwmApi.DwmsbtTransientWindow;
            int acrylicResult = DwmApi.DwmSetWindowAttribute(
                hwnd,
                DwmApi.DwmwaSystemBackdropType,
                ref acrylic,
                sizeof(int));

            if (acrylicResult == 0)
            {
                return true;
            }

            Debug.WriteLine(
                $"[FileKit] Acrylic backdrop failed (HRESULT=0x{acrylicResult:X8}).");
            return false;
        }

        // Windows 11 21H2 (build 22000) exposes Mica through the legacy attribute.
        int legacyMica = 1;
        int legacyResult = DwmApi.DwmSetWindowAttribute(
            hwnd,
            DwmApi.DwmwaMicaEffect,
            ref legacyMica,
            sizeof(int));

        if (legacyResult != 0)
        {
            Debug.WriteLine(
                $"[FileKit] Legacy Mica failed (HRESULT=0x{legacyResult:X8}).");
            return false;
        }

        return true;
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
                // Missing value means the default (enabled); let DWM make the final call.
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

    private static void DisableBackdrop(IntPtr hwnd, int build)
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
                    Debug.WriteLine(
                        $"[FileKit] Could not clear the backdrop (HRESULT=0x{result:X8}).");
                }

                return;
            }

            int disabled = 0;
            int legacyResult = DwmApi.DwmSetWindowAttribute(
                hwnd,
                DwmApi.DwmwaMicaEffect,
                ref disabled,
                sizeof(int));

            if (legacyResult != 0)
            {
                Debug.WriteLine(
                    $"[FileKit] Could not clear the legacy Mica effect (HRESULT=0x{legacyResult:X8}).");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FileKit] Clearing the backdrop threw {ex.GetType().Name}: {ex.Message}");
        }
    }
}
