using System.Runtime.InteropServices;
using Windows.Graphics;

namespace NoHalluTrans.Desktop;

internal static class DisplayScaling
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);

    internal static double Scale(Microsoft.UI.Xaml.Window window) =>
        GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window)) / 96d;

    internal static bool IsPerMonitorV2(Microsoft.UI.Xaml.Window window) =>
        AreDpiAwarenessContextsEqual(
            GetWindowDpiAwarenessContext(WinRT.Interop.WindowNative.GetWindowHandle(window)), new IntPtr(-4));

    // AppWindow sizes are physical pixels; XAML layout dimensions are logical pixels.
    internal static SizeInt32 Pixels(Microsoft.UI.Xaml.Window window, int width, int height) =>
        new((int)Math.Round(width * Scale(window)), (int)Math.Round(height * Scale(window)));
}
