using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Loadout.Core;

namespace Loadout.App;

// Keeps a window's place between runs through Get/SetWindowPlacement: the restored
// rectangle in device pixels, so the window comes back on the same monitor at the same size whatever its scale.
// Windows reports that rectangle in workspace coordinates (the primary monitor's work area at 0,0).
public static class WindowPlacement
{
    private const int ShowHide = 0;
    private const int ShowMinimized = 2;
    private const int ShowMaximized = 3;
    private const int RestoreToMaximized = 0x0002;
    private const uint DefaultToNull = 0;
    private const uint DefaultToPrimary = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Placement
    {
        public int Length;
        public int Flags;
        public int ShowCommand;
        public Point32 MinPosition;
        public Point32 MaxPosition;
        public Rect32 NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect32 Monitor;
        public Rect32 Work;
        public int Flags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(nint hwnd, ref Placement placement);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(nint hwnd, ref Placement placement);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromRect(ref Rect32 rect, uint flags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(Point32 point, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    /// <summary>Whether the rectangle lies at least partly on a monitor that is plugged in now.</summary>
    public static bool IsOnAMonitor(WindowBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        var origin = WorkspaceOrigin();
        var screen = new Rect32
        {
            Left = bounds.Left + origin.X,
            Top = bounds.Top + origin.Y,
            Right = bounds.Left + origin.X + bounds.Width,
            Bottom = bounds.Top + origin.Y + bounds.Height,
        };
        return MonitorFromRect(ref screen, DefaultToNull) != 0;
    }

    /// <summary>
    /// Moves the window to the bounds while it is still hidden: call it from OnSourceInitialized, with
    /// WindowStartupLocation Manual, after <see cref="IsOnAMonitor"/>. Maximizing happens on that monitor.
    /// </summary>
    public static void Restore(Window window, WindowBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(bounds);
        var placement = new Placement
        {
            Length = Marshal.SizeOf<Placement>(),
            ShowCommand = ShowHide,
            NormalPosition = new Rect32
            {
                Left = bounds.Left,
                Top = bounds.Top,
                Right = bounds.Left + bounds.Width,
                Bottom = bounds.Top + bounds.Height,
            },
        };
        if (SetWindowPlacement(new WindowInteropHelper(window).Handle, ref placement) && bounds.Maximized)
        {
            window.WindowState = WindowState.Maximized;
        }
    }

    /// <summary>The window's restored bounds, and whether it is maximized (or minimized from maximized). Null without a handle.</summary>
    public static WindowBounds? Read(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var placement = new Placement { Length = Marshal.SizeOf<Placement>() };
        if (!GetWindowPlacement(new WindowInteropHelper(window).Handle, ref placement))
        {
            return null;
        }

        var normal = placement.NormalPosition;
        var maximized = placement.ShowCommand == ShowMaximized
            || (placement.ShowCommand == ShowMinimized && (placement.Flags & RestoreToMaximized) != 0);
        return new WindowBounds(normal.Left, normal.Top, normal.Right - normal.Left, normal.Bottom - normal.Top, maximized);
    }

    // Where workspace 0,0 is on the screen: the primary monitor's work area corner (a taskbar on the left or top moves it).
    private static Point32 WorkspaceOrigin()
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfo(MonitorFromPoint(default, DefaultToPrimary), ref info)
            ? new Point32 { X = info.Work.Left - info.Monitor.Left, Y = info.Work.Top - info.Monitor.Top }
            : default;
    }
}
