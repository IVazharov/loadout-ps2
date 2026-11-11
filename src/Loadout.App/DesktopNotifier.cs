using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Loadout.Core;
using Forms = System.Windows.Forms;

namespace Loadout.App;

/// <summary>
/// Tells the user what happened while Loadout isn't the window in front: a Windows notification (the balloon of an icon
/// in the notification area, which Windows 10 and 11 show as a notification, with Windows' own sound), and for something
/// that needs the user, a taskbar button that flashes until Loadout comes to the front. The icon stays while the user is
/// away, so the notification can be found again, and goes when Loadout is in front. In front, the page says it already:
/// only a short sound plays for something that needs the user.
/// </summary>
internal sealed class DesktopNotifier : IDisposable
{
    // Windows cuts a balloon's title at 63 characters and its text at 255.
    private const int TitleMax = 63;
    private const int TextMax = 255;

    private readonly Window _window;
    private readonly Action _opened;
    private Forms.NotifyIcon? _icon;

    /// <param name="window">Loadout's window: notifications come only while it isn't in front.</param>
    /// <param name="opened">Called when the user clicks the notification, after the window comes to the front.</param>
    public DesktopNotifier(Window window, Action opened)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _opened = opened ?? throw new ArgumentNullException(nameof(opened));
        _window.Activated += (_, _) => HideIcon();
    }

    public void Show(string title, string text, bool needsUser)
    {
        if (_window.IsActive)
        {
            if (needsUser)
            {
                System.Media.SystemSounds.Exclamation.Play();
            }

            return;
        }

        var icon = _icon ??= CreateIcon();
        icon.Visible = true;
        icon.ShowBalloonTip(10_000, Cut(title, TitleMax), Cut(text, TextMax), needsUser ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
        if (needsUser)
        {
            Flash();
        }
    }

    public void Dispose()
    {
        _icon?.Dispose();
        _icon = null;
    }

    private Forms.NotifyIcon CreateIcon()
    {
        var icon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty),
            Text = ProductInfo.Name,
        };
        icon.BalloonTipClicked += (_, _) => BringForward();
        icon.MouseClick += (_, _) => BringForward();
        return icon;
    }

    private void HideIcon()
    {
        if (_icon is not null)
        {
            _icon.Visible = false;
        }
    }

    private void BringForward()
    {
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
        _opened();
    }

    // Flashes the taskbar button until the window comes to the front.
    private void Flash()
    {
        var info = new NativeMethods.FlashInfo
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.FlashInfo>(),
            Window = new WindowInteropHelper(_window).Handle,
            Flags = NativeMethods.FlashTray | NativeMethods.FlashUntilForeground,
        };
        NativeMethods.FlashWindowEx(ref info);
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private static class NativeMethods
    {
        public const uint FlashTray = 0x2;
        public const uint FlashUntilForeground = 0xC;

        [StructLayout(LayoutKind.Sequential)]
        public struct FlashInfo
        {
            public uint Size;
            public IntPtr Window;
            public uint Flags;
            public uint Count;
            public uint Timeout;
        }

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FlashWindowEx(ref FlashInfo info);
    }
}
