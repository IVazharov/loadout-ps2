using System.Diagnostics;

namespace Loadout.Discs;

/// <summary>The time a disc copy goes by: real time, or a fake one in tests.</summary>
public interface IDiscCopyClock
{
    TimeSpan Elapsed { get; }

    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemDiscCopyClock : IDiscCopyClock
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();

    public TimeSpan Elapsed => _watch.Elapsed;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}
