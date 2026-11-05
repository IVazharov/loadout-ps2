using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Loadout.Persistence;
using Microsoft.Win32;

namespace Loadout.App;

public static class AppTheme
{
    private static ThemeChoice _choice = ThemeChoice.System;
    private static string? _applied;
    private static bool _watching;

    // Raised after a swap with the effective dark flag, for window chrome.
    public static event Action<bool>? Changed;

    // Raised when Windows' app mode or contrast theme changes, whatever the choice, for the Settings help line.
    public static event Action? WindowsModeChanged;

    // A Windows contrast theme is on: the system colours replace both themes.
    public static bool HighContrast => SystemParameters.HighContrast;

    // Whether the dark colours are in use, which a contrast theme replaces, for the caption's glyphs.
    public static bool IsDark => _applied != "Light";

    // Windows' app mode: AppsUseLightTheme 0 is dark; missing (older Windows) or 1 is light.
    public static bool WindowsIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public static void Apply(ThemeChoice choice)
    {
        _choice = choice;
        if (!_watching)
        {
            _watching = true;
            SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
        }

        Apply(Dark(), reload: false);
    }

    // For the main window's hook: Windows broadcasts WM_SETTINGCHANGE with "ImmersiveColorSet" when the
    // app mode changes. Under System the theme follows; under Dark or Light nothing happens.
    // A contrast theme changing to another (Aquatic to Desert) sends WM_SYSCOLORCHANGE only. The brushes follow by
    // themselves; the reload, after WPF has read the new colours, catches the one static Color.
    public static nint OnWindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int SysColorChange = 0x0015;
        const int SettingChange = 0x001A;
        if (message == SysColorChange && SystemParameters.HighContrast)
        {
            Dispatcher.CurrentDispatcher.InvokeAsync(() => Apply(Dark(), reload: true), DispatcherPriority.Background);
            return 0;
        }

        if (message != SettingChange || lParam == 0 || Marshal.PtrToStringUni(lParam) != "ImmersiveColorSet")
        {
            return 0;
        }

        if (_choice == ThemeChoice.System)
        {
            Apply(ThemeChoice.System);
        }

        WindowsModeChanged?.Invoke();

        return 0;
    }

    // Turning a contrast theme on or off. WPF raises this on the UI thread.
    private static void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
        {
            Apply(Dark(), reload: false);
            WindowsModeChanged?.Invoke();
        }
    }

    private static bool Dark() => _choice switch
    {
        ThemeChoice.Dark => true,
        ThemeChoice.Light => false,
        _ => WindowsIsDark(),
    };

    private static Uri Source(string name) => new($"pack://application:,,,/LoadoutPS2;component/Themes/{name}.xaml");

    // Slot 0 of the application's merged dictionaries holds the colours; nothing else is touched. A contrast theme
    // replaces both themes while it is on.
    private static void Apply(bool dark, bool reload)
    {
        var app = Application.Current;
        var name = SystemParameters.HighContrast ? "HighContrast" : dark ? "Dark" : "Light";
        if (app is null || (_applied == name && !reload))
        {
            return;
        }

        var theme = new ResourceDictionary { Source = Source(name) };
        var merged = app.Resources.MergedDictionaries;
        if (merged.Count == 0)
        {
            merged.Add(theme);
        }
        else
        {
            merged[0] = theme;
        }

        _applied = name;
        Changed?.Invoke(dark);
    }
}
