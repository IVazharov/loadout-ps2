using Loadout.Discs;

namespace Loadout.Fakes;

/// <summary>A clock that only moves when told to: by a delay, or by the fake drive's reads.</summary>
internal sealed class FakeDiscClock : IDiscCopyClock
{
    public TimeSpan Elapsed { get; private set; }

    public void Advance(TimeSpan time) => Elapsed += time;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Elapsed += delay;
        return Task.CompletedTask;
    }
}

/// <summary>
/// A disc in a drive that behaves as the one the copy was built on: a failed read takes 7.5 s, and afterwards the drive
/// refuses every read for a while. Sectors can be dead, weak (fail a few times, then read) or dirty (dead until cleaned).
/// Each sector's bytes are made from its number, or come from a given image.
/// </summary>
internal sealed class FakeDiscDrive(long sectors, FakeDiscClock clock, byte[]? content = null) : IDiscReader
{
    private TimeSpan _refuseUntil;

    public string Name => "X:";

    public long Sectors { get; } = sectors;

    public DiscMedia Media { get; init; } = DiscMedia.Dvd;

    public int Layers => 1;

    public long Layer0Sectors => 0;

    public HashSet<long> Dead { get; } = [];

    /// <summary>Sector: how many more reads of it fail before it reads.</summary>
    public Dictionary<long, int> Weak { get; } = [];

    public HashSet<long> Dirty { get; } = [];

    public TimeSpan FailureCost { get; init; } = TimeSpan.FromSeconds(7.5);

    /// <summary>How long the drive refuses every read after a failed one.</summary>
    public TimeSpan Lockout { get; init; } = TimeSpan.FromSeconds(30);

    public double BytesPerSecond { get; init; } = 5_400_000;

    /// <summary>Every read: where, how many sectors, whether it worked.</summary>
    public List<(long Sector, int Count, bool Ok)> Log { get; } = [];

    public int FailedReads { get; private set; }

    /// <summary>Called after each read with the number of reads so far.</summary>
    public Action<int>? AfterRead { get; set; }

    public static byte ByteOf(long sector, int index) => (byte)((sector * 131) + (index * 7) + (sector >> 9));

    public byte[] Expected(long sector, int count)
    {
        var data = new byte[count * DiscSectors.Size];
        Fill(sector, data);
        return data;
    }

    public void Clean() => Dirty.Clear();

    public void RefuseFor(TimeSpan time) => _refuseUntil = clock.Elapsed + time;

    public bool TryRead(long sector, Span<byte> destination, out string? failure)
    {
        var count = destination.Length / DiscSectors.Size;
        var ok = Read(sector, count, destination, out failure);
        Log.Add((sector, count, ok));
        AfterRead?.Invoke(Log.Count);
        return ok;
    }

    public void Dispose()
    {
    }

    private bool Read(long sector, int count, Span<byte> destination, out string? error)
    {
        if (clock.Elapsed < _refuseUntil)
        {
            clock.Advance(TimeSpan.FromMilliseconds(50));
            error = "The device is not ready.";
            return false;
        }

        var bad = false;
        for (var s = sector; s < sector + count; s++)
        {
            if (Dead.Contains(s) || Dirty.Contains(s))
            {
                bad = true;
            }
            else if (Weak.TryGetValue(s, out var left) && left > 0)
            {
                Weak[s] = left - 1;
                bad = true;
            }
        }

        if (bad)
        {
            FailedReads++;
            clock.Advance(FailureCost);
            _refuseUntil = clock.Elapsed + Lockout;
            error = "Data error (cyclic redundancy check).";
            return false;
        }

        Fill(sector, destination);
        clock.Advance(TimeSpan.FromSeconds(destination.Length / BytesPerSecond));
        error = null;
        return true;
    }

    private void Fill(long sector, Span<byte> destination)
    {
        for (var at = 0; at < destination.Length; at += DiscSectors.Size)
        {
            var s = sector + (at / DiscSectors.Size);
            var target = destination.Slice(at, DiscSectors.Size);
            if (content is not null && (s + 1) * DiscSectors.Size <= content.Length)
            {
                content.AsSpan((int)(s * DiscSectors.Size), DiscSectors.Size).CopyTo(target);
                continue;
            }

            for (var i = 0; i < target.Length; i++)
            {
                target[i] = ByteOf(s, i);
            }
        }
    }
}
