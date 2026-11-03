using System.Windows.Threading;

namespace Loadout.App;

/// <summary>
/// One Settings section heading (5.6): a quiet "Saved" for 2 s after a write, or the error when the write failed.
/// </summary>
public sealed class SettingsSection : ObservableObject
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _saved;
    private string _error = string.Empty;

    public SettingsSection(string title)
    {
        Title = title;
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Saved = false;
        };
    }

    public string Title { get; }

    public bool Saved
    {
        get => _saved;
        private set => SetField(ref _saved, value);
    }

    public string Error
    {
        get => _error;
        private set
        {
            if (SetField(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => Error.Length > 0;

    public void ShowSaved()
    {
        Error = string.Empty;
        Saved = true;
        _timer.Stop();
        _timer.Start();
    }

    public void ShowError()
    {
        _timer.Stop();
        Saved = false;
        Error = "Couldn't save. Details are in Logs.";
    }
}
