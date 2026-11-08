using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace CommSpot.Client;

/// <summary>Makes the Windows title bar black. Other systems keep their own title bar.</summary>
internal static partial class DarkTitleBar
{
    private const int UseImmersiveDarkMode = 20;
    private const int UseImmersiveDarkModeOld = 19; // Windows 10 before 20H1
    private const int CaptionColor = 35; // Windows 11
    private const int TextColor = 36; // Windows 11

    [LibraryImport("dwmapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        window.Opened += (_, _) =>
        {
            if (window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero)
            {
                return;
            }
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(hwnd, UseImmersiveDarkMode, ref on, sizeof(int)) != 0)
                {
                    _ = DwmSetWindowAttribute(hwnd, UseImmersiveDarkModeOld, ref on, sizeof(int));
                }
                int black = 0x000000; // COLORREF
                int white = 0xFFFFFF;
                _ = DwmSetWindowAttribute(hwnd, CaptionColor, ref black, sizeof(int));
                _ = DwmSetWindowAttribute(hwnd, TextColor, ref white, sizeof(int));
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
            }
        };
    }
}
