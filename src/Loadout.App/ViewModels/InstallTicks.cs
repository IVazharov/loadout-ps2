using Loadout.Persistence;
using Loadout.Vmc;

namespace Loadout.App;

/// <summary>
/// A page's install ticks: each game's artwork, a memory card for each game and its size, and the check. The Install
/// page and Back up discs each have their own, and each keeps its own as they change.
/// </summary>
public sealed class InstallTicks : ObservableObject
{
    private readonly Action<InstallOptions> _keep;
    private bool _sendArtwork;
    private bool _createMemoryCards;
    private int _memoryCardSize;
    private bool _checkInstalls;

    public InstallTicks(InstallOptions start, Action<InstallOptions> keep)
    {
        ArgumentNullException.ThrowIfNull(start);
        _keep = keep ?? throw new ArgumentNullException(nameof(keep));
        _sendArtwork = start.SendArtwork;
        _createMemoryCards = start.MemoryCards;
        _memoryCardSize = start.MemoryCardSize;
        _checkInstalls = start.Check;
    }

    /// <summary>When set, each game's artwork is downloaded first and sent once the game is installed. A game without any still installs.</summary>
    public bool SendArtwork
    {
        get => _sendArtwork;
        set => Set(ref _sendArtwork, value);
    }

    /// <summary>When set, each game installed gets a virtual memory card in slot 1.</summary>
    public bool CreateMemoryCards
    {
        get => _createMemoryCards;
        set => Set(ref _createMemoryCards, value);
    }

    public IReadOnlyList<int> MemoryCardSizes { get; } = VmcImage.Sizes;

    public int MemoryCardSize
    {
        get => _memoryCardSize;
        set => Set(ref _memoryCardSize, value);
    }

    /// <summary>
    /// When set, Loadout Bay reads each game back from its disk and compares it with what was sent before the game
    /// counts as installed. Slower; the check can be skipped while it runs.
    /// </summary>
    public bool CheckInstalls
    {
        get => _checkInstalls;
        set => Set(ref _checkInstalls, value);
    }

    /// <summary>The ticks as they are now, which the page's installs use.</summary>
    public InstallOptions Options => new(SendArtwork, CreateMemoryCards, MemoryCardSize, CheckInstalls);

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (SetField(ref field, value, name))
        {
            _keep(Options);
        }
    }
}
