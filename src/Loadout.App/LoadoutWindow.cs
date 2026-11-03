using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;

namespace Loadout.App;

// The base of every Loadout window: it keeps the standard caption and colours it (CaptionTheme) before the first show.
public class LoadoutWindow : Window
{
    private nint _handle;

    // An implicit style applies to the exact type only, so a derived window asks for the Window style itself.
    public LoadoutWindow() => SetResourceReference(StyleProperty, typeof(Window));

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _handle = new WindowInteropHelper(this).Handle;
        CaptionTheme.Apply(_handle, active: true);
        HwndSource.FromHwnd(_handle)?.AddHook(OnMessage);
        AppTheme.Changed += OnThemeChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
    }

    protected override void OnClosed(EventArgs e)
    {
        AppTheme.Changed -= OnThemeChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
        base.OnClosed(e);
    }

    // The text colour follows the active state. It is set before the default handler paints the caption for it;
    // set after that (on Activated), it shows only at the next repaint.
    private nint OnMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int NcActivate = 0x0086;
        if (message == NcActivate)
        {
            CaptionTheme.Apply(hwnd, active: wParam != 0);
        }

        return 0;
    }

    // The caption follows the theme, and high contrast coming or going.
    private void OnThemeChanged(bool dark) => Reapply();

    private void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
        {
            Reapply();
        }
    }

    private void Reapply()
    {
        CaptionTheme.Apply(_handle, IsActive);
        CaptionTheme.Repaint(_handle);
    }
}
