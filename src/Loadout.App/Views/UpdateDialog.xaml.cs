using System.ComponentModel;
using System.Windows;

namespace Loadout.App;

// The update offer: an owned modal window. Update now runs in it; when the files are swapped, restart runs.
public partial class UpdateDialog : LoadoutWindow
{
    private readonly UpdateViewModel _model;
    private readonly Action _restart;

    private UpdateDialog(Window owner, UpdateViewModel model, Action restart)
    {
        InitializeComponent();
        _model = model;
        _restart = restart;
        DataContext = model;
        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MaxHeight = owner.ActualHeight * 0.9;
        Loaded += (_, _) => (model.CanUpdate ? UpdateButton : DismissButton).Focus();
    }

    /// <summary>Shows the offer until the user dismisses it, or until the update is in place and Loadout restarts.</summary>
    public static void Show(Window owner, UpdateViewModel model, Action restart) => new UpdateDialog(owner, model, restart).ShowDialog();

    private async void Update_OnClick(object sender, RoutedEventArgs e)
    {
        if (await _model.UpdateAsync())
        {
            _restart();
        }
    }

    private void Dismiss_OnClick(object sender, RoutedEventArgs e)
    {
        if (_model.IsWorking)
        {
            _model.Cancel();
            return;
        }

        Close();
    }

    // Closing the window while it downloads cancels the download; nothing has changed by then.
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_model.IsWorking)
        {
            e.Cancel = _model.IsSwapping;
            _model.Cancel();
        }

        base.OnClosing(e);
    }

    private void ReleasePage_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_model.ReleasePage) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // No browser to open it with; the address is in the button's tooltip.
        }
    }
}
