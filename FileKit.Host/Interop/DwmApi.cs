using System.Runtime.InteropServices;

namespace FileKit.Host.Interop;

/// <summary>
/// Minimal DWM interop used by the host window material service.
/// </summary>
internal static class DwmApi
{
    internal const int DwmwaMicaEffect = 1029;
    internal const int DwmwaSystemBackdropType = 38;
    internal const int DwmsbtMainWindow = 2;
    internal const int DwmsbtTransientWindow = 3;
    internal const int DwmsbtNone = 1;

    [DllImport("dwmapi.dll", ExactSpelling = true, PreserveSig = true)]
    internal static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);

    [DllImport("dwmapi.dll", ExactSpelling = true, PreserveSig = true)]
    internal static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [DllImport("dwmapi.dll", ExactSpelling = true, PreserveSig = true)]
    internal static extern int DwmIsCompositionEnabled(out int compositionEnabled);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Margins
    {
        internal int Left;
        internal int Right;
        internal int Top;
        internal int Bottom;
    }
}
