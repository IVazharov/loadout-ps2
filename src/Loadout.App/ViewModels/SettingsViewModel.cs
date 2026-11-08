using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using Loadout.Bay;
using Loadout.Core;
using Loadout.Persistence;
using Microsoft.Data.Sqlite;

namespace Loadout.App;

/// <summary>What the line under the address says: help or progress, a Bay found, or why none was.</summary>
public enum AddressNoteKind
{
    Plain,
    Found,
    NoneFound,
    NoNetwork,
}

/// <summary>A row of Find Bay's picker: a found Loadout Bay, and whether its address is the one set.</summary>
public sealed record FoundBayRow(FoundBay Bay, bool IsCurrent)
{
    public string Address => Bay.Address.ToString();

    public string Label => "Loadout Bay " + Bay.Version;

    public string AccessibleName => Address + ", " + Label + (IsCurrent ? ", current" : "");
}

// Each setting saves as it changes; there is no Save button.
public sealed class SettingsViewModel : ObservableObject
{
    private const string AddressHelp = "The address Loadout Bay shows on the TV.";

    private const string EnterAddress = "Enter the PS2's address, or Find Bay.";

    private readonly AppSettingsStore _store;
    private readonly InstallViewModel _install;
    private readonly OperationLog _log;
    private string _host;
    private string _addressNote;
    private AddressNoteKind _noteKind;
    private string _cutNote = "";
    private ThemeChoice _theme;
    private bool _checkForUpdates;
    private bool _finding;
    private bool _choosing;
    private CancellationTokenSource? _findRun;
    private bool _windowsDark = AppTheme.WindowsIsDark();

    public SettingsViewModel(AppSettingsStore store, AppSettings stored, InstallViewModel install, OperationLog log)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(stored);
        _install = install ?? throw new ArgumentNullException(nameof(install));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        // A host name saved by an older Loadout can't go in the editor; the editor starts empty.
        _host = Ipv4Text.IsValid(stored.Ps2Host) ? stored.Ps2Host.Trim() : string.Empty;
        _addressNote = HelpNote;
        _theme = stored.Theme;
        _checkForUpdates = stored.CheckForUpdates;
        FindBayCommand = new AsyncRelayCommand(FindBayAsync, ex => Report(ex.Message));
        CancelFindCommand = new RelayCommand(CancelFind);
        // The user's own input wins over a search, and ends what the last one said.
        AddressEditedCommand = new RelayCommand(() =>
        {
            CancelFind();
            if (_noteKind != AddressNoteKind.Plain)
            {
                SetNote(AddressNoteKind.Plain, HelpNote);
            }
        });
        AppTheme.WindowsModeChanged += () =>
        {
            _windowsDark = AppTheme.WindowsIsDark();
            OnPropertyChanged(nameof(ThemeHelp));
        };
    }

    public SettingsSection Ps2 { get; } = new("PS2");

    public SettingsSection InstallSection { get; } = new("Install");

    public SettingsSection Appearance { get; } = new("Appearance");

    public SettingsSection Updates { get; } = new("Updates");

    /// <summary>Whether Loadout asks GitHub for a newer version when it starts. The update dialog changes it too.</summary>
    public bool CheckForUpdates
    {
        get => _checkForUpdates;
        set
        {
            if (value == _checkForUpdates)
            {
                return;
            }

            _checkForUpdates = value;
            OnPropertyChanged();
            Save(Updates, settings => settings with { CheckForUpdates = value });
            _log.Add(value ? "Loadout checks for updates when it starts." : "Loadout no longer checks for updates when it starts.");
        }
    }

    public ICommand FindBayCommand { get; }

    public ICommand CancelFindCommand { get; }

    public ICommand AddressEditedCommand { get; }

    /// <summary>The Loadout Bays the last search found, when it found more than one, sorted by address.</summary>
    public ObservableCollection<FoundBayRow> FoundBays { get; } = [];

    /// <summary>True while the picker of several found Loadout Bays is open. Closing it without a choice changes nothing.</summary>
    public bool IsChoosing
    {
        get => _choosing;
        set
        {
            if (SetField(ref _choosing, value) && !value && FoundBays.Count > 0)
            {
                FoundBays.Clear();
                if (_noteKind == AddressNoteKind.Plain)
                {
                    SetNote(AddressNoteKind.Plain, HelpNote);
                }
            }
        }
    }

    public bool IsFinding
    {
        get => _finding;
        private set
        {
            if (SetField(ref _finding, value))
            {
                OnPropertyChanged(nameof(CanFind));
                OnPropertyChanged(nameof(FindLabel));
                OnPropertyChanged(nameof(FindStatus));
            }
        }
    }

    public bool CanFind => !_finding;

    public string FindLabel => _finding ? "Searching…" : "_Find Bay";

    /// <summary>The button's UIA item status; its name stays "Find Bay".</summary>
    public string FindStatus => _finding ? "Searching" : "";

    /// <summary>The PS2's IPv4 address, or empty while none is set. The editor only ever sets a valid one.</summary>
    public string Host
    {
        get => _host;
        set
        {
            if (!Ipv4Text.IsValid(value) || !SetField(ref _host, value.Trim()))
            {
                return;
            }

            SetNote(AddressNoteKind.Plain, AddressHelp);
            if (Save(Ps2, settings => settings with { Ps2Host = _host }))
            {
                _log.Add(string.Create(CultureInfo.InvariantCulture, $"PS2 address set to {_host}."));
            }
        }
    }

    /// <summary>The line under the address: its help, Find Bay's progress, or what it found.</summary>
    public string AddressNote
    {
        get => _addressNote;
        private set => SetField(ref _addressNote, value);
    }

    public bool NoteIsPlain => _noteKind == AddressNoteKind.Plain;

    public bool NoteIsFound => _noteKind == AddressNoteKind.Found;

    public bool NoteIsNoneFound => _noteKind == AddressNoteKind.NoneFound;

    /// <summary>True when the note says nothing answered, or that there is no network: both offer Search again.</summary>
    public bool NoteOffersSearch => _noteKind is AddressNoteKind.NoneFound or AddressNoteKind.NoNetwork;

    /// <summary>After a search that found nothing: which part of a larger network was swept, or empty.</summary>
    public string CutNote
    {
        get => _cutNote;
        private set
        {
            if (SetField(ref _cutNote, value))
            {
                OnPropertyChanged(nameof(HasCutNote));
            }
        }
    }

    public bool HasCutNote => _cutNote.Length > 0;

    private string HelpNote => _host.Length == 0 ? EnterAddress : AddressHelp;

    public bool CheckInstalls
    {
        get => _install.CheckInstalls;
        set
        {
            if (value == _install.CheckInstalls)
            {
                return;
            }

            _install.CheckInstalls = value;
            OnPropertyChanged();
            Save(InstallSection, settings => settings with { CheckInstalls = value });
        }
    }

    public ThemeChoice Theme
    {
        get => _theme;
        set
        {
            if (!SetField(ref _theme, value))
            {
                return;
            }

            AppTheme.Apply(value);
            Save(Appearance, settings => settings with { Theme = value });
        }
    }

    public string ThemeHelp => AppTheme.HighContrast
        ? "A Windows contrast theme is on, so Loadout uses its colours."
        : _windowsDark
            ? "System follows Windows' app mode (now dark)."
            : "System follows Windows' app mode (now light).";

    // Writes one change over what is stored. An old host name the editor couldn't show is not kept.
    private bool Save(SettingsSection section, Func<AppSettings, AppSettings> change)
    {
        try
        {
            var stored = _store.Load();
            if (!Ipv4Text.IsValid(stored.Ps2Host))
            {
                stored = stored with { Ps2Host = _host };
            }

            _store.Save(change(stored));
            section.ShowSaved();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or SqliteException or IOException or UnauthorizedAccessException)
        {
            section.ShowError();
            _log.Add("Couldn't save the settings: " + ex.Message);
            return false;
        }
    }

    // The probe, then the sweep, with the progress in the note.
    private async Task FindBayAsync()
    {
        CancelFind();
        IsChoosing = false;
        var run = new CancellationTokenSource();
        _findRun = run;
        IsFinding = true;
        SetNote(AddressNoteKind.Plain, BaySearch.Asking);
        BaySearchResult result;
        try
        {
            result = await BaySearch.RunAsync(
                note =>
                {
                    if (_findRun == run)
                    {
                        SetNote(AddressNoteKind.Plain, note);
                    }
                },
                run.Token);
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            if (_findRun == run)
            {
                _findRun = null;
                IsFinding = false;
            }

            run.Dispose();
        }

        if (result.NoNetwork)
        {
            CutNote = "";
            SetNote(AddressNoteKind.NoNetwork, BaySearch.NoNetwork);
            _log.Add("Find Bay: " + BaySearch.NoNetwork);
        }
        else if (result.Found.Count == 0)
        {
            CutNote = result.CutNote;
            SetNote(AddressNoteKind.NoneFound, "No Loadout Bay answered. Check that:");
            _log.Add("Find Bay: no Loadout Bay answered." + (result.CutNote.Length > 0 ? " " + result.CutNote : ""));
        }
        else if (result.Found.Count == 1)
        {
            Use(result.Found[0]);
        }
        else
        {
            foreach (var bay in result.Found)
            {
                FoundBays.Add(new FoundBayRow(bay, BaySearch.IsAt(bay, _host)));
            }

            Report(string.Create(CultureInfo.CurrentCulture, $"Found {result.Found.Count} Loadout Bays. Choose one."));
            IsChoosing = true;
        }
    }

    // Stops a search at once; what it still finds is dropped, and the note shows the help again.
    private void CancelFind()
    {
        if (_findRun is not { } run)
        {
            return;
        }

        _findRun = null;
        run.Cancel();
        IsFinding = false;
        SetNote(AddressNoteKind.Plain, HelpNote);
        _log.Add("Find Bay cancelled.");
    }

    /// <summary>The picker's choice: its address is set and saved.</summary>
    public void Choose(FoundBayRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        Use(row.Bay, chosen: true);
        IsChoosing = false;
    }

    /// <summary>Sets and saves a found Loadout Bay's address, and says so in the note.</summary>
    public void Use(FoundBay bay, bool chosen = false)
    {
        ArgumentNullException.ThrowIfNull(bay);
        var address = bay.Address.ToString();
        var already = address == Host;
        Host = address;
        var note = chosen ? string.Create(CultureInfo.InvariantCulture, $"Using Loadout Bay {bay.Version} at {address}.")
            : already ? string.Create(CultureInfo.InvariantCulture, $"Loadout Bay {bay.Version} is at {address}, the address already set.")
            : string.Create(CultureInfo.InvariantCulture, $"Found Loadout Bay {bay.Version} at {address}.");
        SetNote(AddressNoteKind.Found, note);
        _log.Add(note);
    }

    private void Report(string note)
    {
        SetNote(AddressNoteKind.Plain, note);
        _log.Add(note);
    }

    private void SetNote(AddressNoteKind kind, string note)
    {
        AddressNote = note;
        if (_noteKind != kind)
        {
            _noteKind = kind;
            OnPropertyChanged(nameof(NoteIsPlain));
            OnPropertyChanged(nameof(NoteIsFound));
            OnPropertyChanged(nameof(NoteIsNoneFound));
            OnPropertyChanged(nameof(NoteOffersSearch));
        }
    }
}
