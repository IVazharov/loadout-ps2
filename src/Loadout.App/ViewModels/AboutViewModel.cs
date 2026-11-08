using System.Globalization;
using System.IO;
using System.Windows.Input;
using Loadout.Core;
using Loadout.Updates;

namespace Loadout.App;

/// <summary>Which text the About page's licence box shows.</summary>
public enum AboutText
{
    Licence,
    Notices,
}

/// <summary>
/// The About page: the version and its author, Check for updates, links, the data sources, and the licence texts.
/// </summary>
public sealed class AboutViewModel : ObservableObject, IDisposable
{
    public const string RepositoryUrl = "https://github.com/IVazharov/loadout-ps2";
    public const string IssuesUrl = RepositoryUrl + "/issues";
    public const string ChangelogUrl = RepositoryUrl + "/blob/main/CHANGELOG.md";
    public const string TitleDatabaseUrl = "https://github.com/Thecat1080/PS2-OPL-CFG-Database";
    public const string ArtworkDatabaseUrl = "https://github.com/Luden02/psx-ps2-opl-art-database";

    private readonly GitHubReleaseFeed _releases;
    private readonly OperationLog _log;
    private readonly AsyncRelayCommand _check;
    private bool _checking;
    private string _checkResult = string.Empty;
    private bool _checkFailed;
    private UpdateOffer? _offer;
    private AboutText _shown = AboutText.Licence;
    private string? _licence;
    private string? _notices;

    public AboutViewModel(GitHubReleaseFeed releases, OperationLog log)
    {
        _releases = releases ?? throw new ArgumentNullException(nameof(releases));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _check = new AsyncRelayCommand(CheckAsync);
    }

    public static string Version => "Loadout " + ProductInfo.Version;

    public static string Author => "by Ivan Vazharov";

    public ICommand CheckCommand => _check;

    public bool IsChecking
    {
        get => _checking;
        private set
        {
            if (SetField(ref _checking, value))
            {
                OnPropertyChanged(nameof(CanCheck));
            }
        }
    }

    public bool CanCheck => !IsChecking;

    /// <summary>The last check's outcome, empty before the first one.</summary>
    public string CheckResult
    {
        get => _checkResult;
        private set => SetField(ref _checkResult, value);
    }

    public bool CheckFailed
    {
        get => _checkFailed;
        private set => SetField(ref _checkFailed, value);
    }

    /// <summary>The newer version the last check found, or null.</summary>
    public UpdateOffer? Offer
    {
        get => _offer;
        private set
        {
            if (SetField(ref _offer, value))
            {
                OnPropertyChanged(nameof(HasOffer));
                OnPropertyChanged(nameof(OfferPage));
            }
        }
    }

    public bool HasOffer => Offer is not null;

    public string OfferPage => Offer?.Release.PageUrl.AbsoluteUri ?? string.Empty;

    public AboutText Shown
    {
        get => _shown;
        set
        {
            if (SetField(ref _shown, value))
            {
                OnPropertyChanged(nameof(ShownText));
            }
        }
    }

    /// <summary>Loadout's licence or the third-party notices, read from the executable when first shown.</summary>
    public string ShownText => Shown == AboutText.Licence
        ? _licence ??= Embedded("LICENSE")
        : _notices ??= Embedded("THIRD-PARTY-NOTICES.txt");

    public void Dispose() => _check.Dispose();

    /// <summary>After an update's restart: says which version this one replaced.</summary>
    public void ShowUpdated(string oldVersion)
    {
        CheckResult = "Updated from " + oldVersion + ".";
        _log.Add("Loadout updated from " + oldVersion + " to " + ProductInfo.Version + ".");
    }

    /// <summary>Looks for a newer release on GitHub and says what it found.</summary>
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        IsChecking = true;
        CheckFailed = false;
        CheckResult = "Checking GitHub for a newer version…";
        try
        {
            var releases = await _releases.ListAsync(cancellationToken);
            Offer = UpdateCheck.Choose(releases, AppVersion.Parse(ProductInfo.Version));
            var at = DateTime.Now.ToString("t", CultureInfo.CurrentCulture);
            CheckResult = Offer is null
                ? "This is the newest version. Checked at " + at + "."
                : "Loadout " + Offer.Version + " is out. Checked at " + at + ".";
            _log.Add(Offer is null
                ? "Check for updates: " + ProductInfo.Version + " is the newest version."
                : "Check for updates: Loadout " + Offer.Version + " is out; this is " + ProductInfo.Version + ".");
        }
        catch (UpdateCheckException ex)
        {
            CheckFailed = true;
            CheckResult = "Couldn't check. " + ex.Message;
            _log.Add("Check for updates failed: " + ex.Message);
        }
        finally
        {
            IsChecking = false;
        }
    }

    private static string Embedded(string name)
    {
        using var stream = typeof(AboutViewModel).Assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            return "This text is in " + name + " next to Loadout, and on " + RepositoryUrl + ".";
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
