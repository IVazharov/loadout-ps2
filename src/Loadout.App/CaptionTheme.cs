using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace Loadout.App;

// Paints the standard caption through DWM in the theme's frame colour, so caption and rail read as one frame.
// Windows 11 takes all the colours; Windows 10 (1809 and later) only the dark mode, which gives a dark grey caption
// in the dark theme and its own light one in the light theme.
public static class CaptionTheme
{
    private const int DarkModeOld = 19;
    private const int DarkMode = 20;
    private const int BorderColor = 34;
    private const int CaptionColor = 35;
    private const int TextColor = 36;
    private const int ColorDefault = unchecked((int)0xFFFFFFFF);

    private const uint FrameChanged = 0x0020;
    private const uint NoSizeMoveZOrderActivate = 0x0001 | 0x0002 | 0x0004 | 0x0010;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);

    // With high contrast on, everything goes back to the system's defaults.
    public static void Apply(nint hwnd, bool active)
    {
        if (hwnd == 0)
        {
            return;
        }

        var highContrast = SystemParameters.HighContrast;
        var darkGlyphs = !highContrast && AppTheme.IsDark ? 1 : 0;
        if (Set(hwnd, DarkMode, darkGlyphs) != 0)
        {
            Set(hwnd, DarkModeOld, darkGlyphs);
        }

        // Attributes 34 to 36 exist from Windows 11; earlier versions refuse them, which is fine.
        Set(hwnd, CaptionColor, highContrast ? ColorDefault : ColorRef("AppBandColor"));
        Set(hwnd, BorderColor, highContrast ? ColorDefault : ColorRef("AppBorderColor"));
        Set(hwnd, TextColor, highContrast ? ColorDefault : ColorRef(active ? "AppTextColor" : "AppLabelColor"));
    }

    // Windows 10 repaints the caption for a new dark mode only when the frame changes.
    public static void Repaint(nint hwnd)
    {
        if (hwnd != 0)
        {
            SetWindowPos(hwnd, 0, 0, 0, 0, 0, FrameChanged | NoSizeMoveZOrderActivate);
        }
    }

    private static int Set(nint hwnd, int attribute, int value) =>
        DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));

    // COLORREF is 0x00BBGGRR.
    private static int ColorRef(string key)
    {
        // Before the first theme is applied there is no colour yet: Windows' default until then.
        return Application.Current?.TryFindResource(key) is Color color
            ? color.R | (color.G << 8) | (color.B << 16)
            : ColorDefault;
    }
}
