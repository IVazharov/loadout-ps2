using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Loadout.Bay;
using Loadout.Discs;
using Loadout.Images;

namespace Loadout.SpeedTest;

/// <summary>The disc-drive commands: what is in the drive, and reading all of it.</summary>
public static class DiscCommands
{
    /// <summary>Prints what the drive and Windows say about the disc, then what Loadout reads from its file system.</summary>
    public static int Info(string driveName)
    {
        var clock = Stopwatch.StartNew();
        using var drive = DiscDrive.Open(driveName);
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Drive {drive.Name} opened in {clock.ElapsedMilliseconds} ms; profile {(drive.Profile is { } p ? $"0x{p:X4} ({ProfileName(p)})" : "unknown")}; {drive.Length} bytes = {drive.Length / DiscDrive.SectorSize} sectors."));
        if (drive.Layers is { } layers)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"DVD: {layers.Layers} layer(s), {(layers.OppositeTrackPath ? "opposite" : "parallel")} track path, physical sectors 0x{layers.StartSector:X}-0x{layers.EndSector:X}, layer 0 ends at 0x{layers.EndLayer0Sector:X}{(layers.Layers > 1 ? $" ({layers.Layer0Sectors} sectors on layer 0)" : "")}."));
        }

        using var stream = new DiscDriveStream(drive, drive.Length);
        clock.Restart();
        var facts = Iso9660Image.Read(stream, drive.Name, SectorLayout.Headerless2048, drive.IsCd);
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"File system read in {clock.ElapsedMilliseconds} ms: label {facts.VolumeLabel ?? "-"}, disc ID {facts.DiscId?.Canonical ?? "-"}, {facts.Media}, {facts.Layer0Sectors} + {facts.Layer1Sectors} sectors{(facts.Failure is null ? "" : $"; {facts.Failure}")}."));
        return facts.Readable && facts.DiscId is not null ? 0 : 1;
    }

    /// <summary>Reads the whole disc as an install would, timing it and hashing it the way Redump lists discs.</summary>
    public static int Read(string driveName, string[] options)
    {
        string? output = null;
        var zeroFill = false;
        ushort? speed = null;
        for (var i = 0; i < options.Length; i++)
        {
            switch (options[i])
            {
                case "--out" when i + 1 < options.Length:
                    output = options[++i];
                    break;
                case "--zero-fill":
                    zeroFill = true;
                    break;
                case "--speed" when i + 1 < options.Length:
                    speed = ushort.Parse(options[++i], CultureInfo.InvariantCulture);
                    break;
                default:
                    return 2;
            }
        }

        using var drive = DiscDrive.Open(driveName);
        if (speed is { } kilobytes)
        {
            Console.WriteLine(drive.SetReadSpeed(kilobytes) ? $"Read speed set to {kilobytes} KB/s." : "The drive refused the read speed.");
        }

        var clock = Stopwatch.StartNew();
        using var stream = new DiscDriveStream(drive, drive.Length) { ZeroFill = zeroFill };
        stream.Log.Event = line => Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  [{clock.Elapsed.TotalSeconds,7:0.0} s] {line}"));
        var facts = Iso9660Image.Read(stream, drive.Name, SectorLayout.Headerless2048, drive.IsCd);
        var total = ((long)facts.Layer0Sectors + facts.Layer1Sectors) * DiscDrive.SectorSize;
        if (!facts.Readable || total == 0)
        {
            Console.Error.WriteLine($"The disc's file system could not be read: {facts.Failure}");
            return 1;
        }

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{facts.DiscId?.Canonical ?? facts.VolumeLabel} | {facts.Media} | {facts.Layer0Sectors} + {facts.Layer1Sectors} sectors = {total} bytes (Windows says {drive.Length})"));

        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var file = output is null ? null : new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
        var buffer = new byte[1 << 20];
        stream.Position = 0;
        clock.Restart();
        long done = 0;
        var nextLine = 0L;
        var lineStart = TimeSpan.Zero;
        var lineBytes = 0L;
        while (done < total)
        {
            var n = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, total - done));
            if (n == 0)
            {
                Console.Error.WriteLine($"The disc ended after {done} of {total} bytes.");
                return 1;
            }

            md5.AppendData(buffer, 0, n);
            sha1.AppendData(buffer, 0, n);
            file?.Write(buffer, 0, n);
            done += n;
            if (done >= nextLine || done == total)
            {
                var now = clock.Elapsed;
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  {done * 100.0 / total,5:0.0}%  {done / SpeedTestWire.BytesPerMegabyte,6:0} MB  {(done - lineBytes) / SpeedTestWire.BytesPerMegabyte / Math.Max((now - lineStart).TotalSeconds, 0.001),5:0.0} MB/s now  {done / SpeedTestWire.BytesPerMegabyte / Math.Max(now.TotalSeconds, 0.001),5:0.0} MB/s overall"));
                lineStart = now;
                lineBytes = done;
                nextLine = done + (total / 20);
            }
        }

        var log = stream.Log;
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Read {total} bytes in {clock.Elapsed.TotalSeconds:0.0} s, {total / SpeedTestWire.BytesPerMegabyte / clock.Elapsed.TotalSeconds:0.0} MB/s. Retries {log.Retries}, slow reads {log.SlowReads}, slowest {log.SlowestRead.TotalSeconds:0.0} s, unreadable sectors {log.BadSectors.Count}."));
        Console.WriteLine($"MD5 {Convert.ToHexStringLower(md5.GetHashAndReset())}  SHA-1 {Convert.ToHexStringLower(sha1.GetHashAndReset())}");
        return log.BadSectors.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// Maps the parts of a disc that cannot be read. Reads each part once; past a failed read it jumps ahead in growing
    /// steps until a sector reads, then finds where the bad patch ends, so a scratch costs a few failed reads, not one per sector.
    /// </summary>
    public static int Map(string driveName, string[] options)
    {
        long from = 0;
        long? to = null;
        ushort? speed = null;
        var recover = "none";
        int? streamSpeed = null;
        var chunk = 128;
        for (var i = 0; i < options.Length; i++)
        {
            switch (options[i])
            {
                case "--chunk" when i + 1 < options.Length:
                    chunk = int.Parse(options[++i], CultureInfo.InvariantCulture);
                    break;
                case "--from" when i + 1 < options.Length:
                    from = long.Parse(options[++i], CultureInfo.InvariantCulture);
                    break;
                case "--to" when i + 1 < options.Length:
                    to = long.Parse(options[++i], CultureInfo.InvariantCulture);
                    break;
                case "--speed" when i + 1 < options.Length:
                    speed = ushort.Parse(options[++i], CultureInfo.InvariantCulture);
                    break;
                case "--stream-speed" when i + 1 < options.Length:
                    streamSpeed = int.Parse(options[++i], CultureInfo.InvariantCulture);
                    break;
                case "--recover" when i + 1 < options.Length:
                    recover = options[++i];
                    break;
                default:
                    return 2;
            }
        }

        using var drive = DiscDrive.Open(driveName);
        if (speed is { } kilobytes)
        {
            Console.WriteLine(drive.SetReadSpeed(kilobytes) ? $"Read speed set to {kilobytes} KB/s." : "The drive refused the read speed.");
        }

        if (streamSpeed is { } streaming)
        {
            Console.WriteLine(drive.SetStreamingSpeed(streaming) ? $"Streaming speed set to {streaming} KB/s." : $"The drive refused a streaming speed: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");
        }

        var end = Math.Min(to ?? long.MaxValue, drive.Length / DiscDrive.SectorSize);
        var buffer = GC.AllocateUninitializedArray<byte>(chunk * DiscDrive.SectorSize, pinned: true);
        var clock = Stopwatch.StartNew();
        var patches = new List<(long First, long End)>();
        void Note(string line) => Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  [{clock.Elapsed.TotalSeconds,7:0.0} s] {line}"));
        bool Reads(long sector)
        {
            var started = clock.Elapsed;
            var error = drive.TryRead(sector, buffer.AsSpan(0, DiscDrive.SectorSize));
            Note(string.Create(CultureInfo.InvariantCulture, $"sector {sector}: {(error is null ? "read" : "unreadable")} in {(clock.Elapsed - started).TotalSeconds:0.0} s"));
            return error is null;
        }

        var sector = from;
        var nextLine = from;
        while (sector < end)
        {
            if (sector >= nextLine)
            {
                Note(string.Create(CultureInfo.InvariantCulture, $"at sector {sector} ({(sector - from) * 100.0 / Math.Max(end - from, 1):0}%)"));
                nextLine = sector + Math.Max((end - from) / 20, chunk);
            }

            var count = (int)Math.Min(chunk, end - sector);
            var started = clock.Elapsed;
            var error = drive.TryRead(sector, buffer.AsSpan(0, count * DiscDrive.SectorSize));
            var took = clock.Elapsed - started;
            if (error is null)
            {
                if (took.TotalSeconds >= 1)
                {
                    Note(string.Create(CultureInfo.InvariantCulture, $"slow: {count} sectors at sector {sector} took {took.TotalSeconds:0.0} s"));
                }

                sector += count;
                continue;
            }

            Note(string.Create(CultureInfo.InvariantCulture, $"{count} sectors at sector {sector} failed in {took.TotalSeconds:0.0} s: {error}"));
            if (recover != "none")
            {
                // An experiment: does the drive read again after it is opened afresh, or after a seek to the start?
                started = clock.Elapsed;
                if (recover == "restart")
                {
                    Note($"spindle restart: {drive.RestartSpindle() ?? "done"}");
                }
                else if (recover == "reopen")
                {
                    drive.Reopen();
                }
                else if (recover.StartsWith("wait:", StringComparison.Ordinal))
                {
                    Thread.Sleep(TimeSpan.FromSeconds(double.Parse(recover.AsSpan(5), CultureInfo.InvariantCulture)));
                }
                else
                {
                    _ = drive.TryRead(16, buffer.AsSpan(0, DiscDrive.SectorSize));
                }

                error = drive.TryRead(sector, buffer.AsSpan(0, count * DiscDrive.SectorSize));
                Note(string.Create(CultureInfo.InvariantCulture, $"after {recover} ({(clock.Elapsed - started).TotalSeconds:0.0} s) the same read {(error is null ? "worked" : "failed: " + error)}"));
                if (error is null)
                {
                    sector += count;
                    continue;
                }
            }

            var first = sector;
            while (first < sector + count && Reads(first))
            {
                first++;
            }

            if (first == sector + count)
            {
                sector += count;
                continue;
            }

            // Jump ahead in growing steps until a sector reads, then halve the gap to find the first one after the patch.
            var lastBad = first;
            var step = 1L;
            long good;
            while (true)
            {
                good = Math.Min(lastBad + step, end);
                if (good == end || Reads(good))
                {
                    break;
                }

                lastBad = good;
                step *= 2;
            }

            while (good - lastBad > 1)
            {
                var middle = lastBad + ((good - lastBad) / 2);
                if (Reads(middle))
                {
                    good = middle;
                }
                else
                {
                    lastBad = middle;
                }
            }

            patches.Add((first, good));
            Note(string.Create(CultureInfo.InvariantCulture, $"UNREADABLE: sectors {first}-{good - 1} ({good - first} sectors)"));
            sector = good;
        }

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Mapped sectors {from}-{end - 1} in {clock.Elapsed.TotalSeconds:0.0} s: {patches.Count} unreadable patch(es), {patches.Sum(p => p.End - p.First)} sectors."));
        foreach (var (first, after) in patches)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {first}-{after - 1}"));
        }

        return patches.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// An experiment: reads a range in chunks, in order or strided (every n-th chunk, then the ones after them), with a pause
    /// after each read; after a failed read it rests the drive, then tries that chunk once more.
    /// </summary>
    public static int Probe(string driveName, string[] options)
    {
        long from = 0;
        long? to = null;
        var chunk = 128;
        var gap = 0;
        var stride = 1;
        var rest = 60;
        for (var i = 0; i + 1 < options.Length; i += 2)
        {
            var value = long.Parse(options[i + 1], CultureInfo.InvariantCulture);
            switch (options[i])
            {
                case "--from": from = value; break;
                case "--to": to = value; break;
                case "--chunk": chunk = (int)value; break;
                case "--gap": gap = (int)value; break;
                case "--stride": stride = (int)value; break;
                case "--rest": rest = (int)value; break;
                default: return 2;
            }
        }

        if (options.Length % 2 != 0)
        {
            return 2;
        }

        using var drive = DiscDrive.Open(driveName);
        var end = Math.Min(to ?? long.MaxValue, drive.Length / DiscDrive.SectorSize);
        var chunks = (end - from + chunk - 1) / chunk;
        var order = Enumerable.Range(0, stride).SelectMany(k => LongRange(k, chunks, stride)).ToList();
        var buffer = GC.AllocateUninitializedArray<byte>(chunk * DiscDrive.SectorSize, pinned: true);
        var clock = Stopwatch.StartNew();
        int good = 0, failed = 0, recovered = 0;
        var resting = TimeSpan.Zero;
        void Note(string line) => Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  [{clock.Elapsed.TotalSeconds,7:0.0} s] {line}"));
        for (var n = 0; n < order.Count; n++)
        {
            var sector = from + (order[n] * chunk);
            var count = (int)Math.Min(chunk, end - sector);
            var started = clock.Elapsed;
            var error = drive.TryRead(sector, buffer.AsSpan(0, count * DiscDrive.SectorSize));
            var took = clock.Elapsed - started;
            if (error is null)
            {
                good++;
                if (took.TotalSeconds >= 1)
                {
                    Note(string.Create(CultureInfo.InvariantCulture, $"slow: sector {sector} took {took.TotalSeconds:0.0} s"));
                }
            }
            else
            {
                failed++;
                Note(string.Create(CultureInfo.InvariantCulture, $"FAILED at sector {sector} after {good} good reads: {error}; resting {rest} s"));
                var restStart = clock.Elapsed;
                Thread.Sleep(TimeSpan.FromSeconds(rest));
                resting += clock.Elapsed - restStart;
                error = drive.TryRead(sector, buffer.AsSpan(0, count * DiscDrive.SectorSize));
                Note($"  retry {(error is null ? "worked" : "failed")}");
                if (error is null)
                {
                    recovered++;
                }
            }

            if (n % Math.Max(order.Count / 10, 1) == 0)
            {
                Note(string.Create(CultureInfo.InvariantCulture, $"{n * 100 / order.Count}% of the reads done"));
            }

            if (gap > 0)
            {
                Thread.Sleep(gap);
            }
        }

        var bytes = (end - from) * DiscDrive.SectorSize;
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{order.Count} reads of {chunk} sectors (stride {stride}, gap {gap} ms): {good} good, {failed} failed, {recovered} worked after a rest; {clock.Elapsed.TotalSeconds:0} s, {resting.TotalSeconds:0} s of it resting; {bytes / SpeedTestWire.BytesPerMegabyte / clock.Elapsed.TotalSeconds:0.00} MB/s."));
        return failed - recovered == 0 ? 0 : 1;

        static IEnumerable<long> LongRange(long first, long count, long step)
        {
            for (var x = first; x < count; x += step)
            {
                yield return x;
            }
        }
    }

    /// <summary>
    /// Backs up a disc to an image with Loadout's disc copier (<see cref="DiscCopier"/>), going on from the map beside the
    /// image when there is one, then hashes a complete copy the way Redump lists discs. Ctrl+C stops it; running it again
    /// goes on from where it was. --cleaned after cleaning the disc, --another-drive when the disc is in another drive:
    /// both start the passes over what is missing in big pieces.
    /// </summary>
    public static int Copy(string driveName, string[] options)
    {
        if (options.Length == 0 || !OperatingSystem.IsWindows())
        {
            return 2;
        }

        var imagePath = Path.GetFullPath(options[0]);
        TimeSpan? limit = null;
        bool? cleaned = null;
        for (var i = 1; i < options.Length; i++)
        {
            switch (options[i])
            {
                case "--minutes" when i + 1 < options.Length:
                    limit = TimeSpan.FromMinutes(double.Parse(options[++i], CultureInfo.InvariantCulture));
                    break;
                case "--cleaned":
                    cleaned = true;
                    break;
                case "--another-drive":
                    cleaned = false;
                    break;
                default:
                    return 2;
            }
        }

        var mapPath = imagePath + ".map";
        var clock = Stopwatch.StartNew();
        void Note(string line) => Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[{clock.Elapsed:hh\\:mm\\:ss}] {line}"));

        using var drive = WindowsDiscDrive.Open(driveName);
        Note($"{drive.Name}: {drive.Media}, {drive.Sectors} sectors ({Mb(drive.Sectors)}), {drive.Layers} layer(s).");

        // A drive can still be refusing reads after a failed one (a minute or less); give it time before calling the disc unreadable.
        var identity = DiscIdentity.Read(drive);
        for (var attempt = 1; identity is null && attempt <= 4; attempt++)
        {
            Note("The disc's volume descriptor did not read; waiting 30 s for the drive.");
            Thread.Sleep(TimeSpan.FromSeconds(30));
            identity = DiscIdentity.Read(drive);
        }

        if (identity is null)
        {
            Console.Error.WriteLine("The disc's volume descriptor could not be read.");
            return 1;
        }

        DiscCopyMap map;
        if (File.Exists(mapPath))
        {
            map = DiscCopyMap.Load(mapPath);
            if (!DiscIdentity.TryParse(map.Identity, out var was) || !was.IsSameDisc(identity))
            {
                Console.Error.WriteLine($"{mapPath} is for another disc ({map.Identity}); this one is {identity.Format()}.");
                return 1;
            }

            Note($"Going on with the copy of {identity.Format()}: {Mb(map.ReadSectors)} of {Mb(map.Sectors)} done, pass {map.Sweep + 1}.");
            if (cleaned is { } wasCleaned)
            {
                map.StartOver(wasCleaned);
                Note(wasCleaned ? $"After cleaning {map.Cleanings}: the passes start over in big pieces." : "In another drive: the passes start over in big pieces.");
            }
        }
        else
        {
            map = DiscCopyMap.New(drive.Sectors, identity.Format());
            Note($"New copy of {identity.Format()} to {imagePath}");
        }

        using var stop = limit is { } time ? new CancellationTokenSource(time) : new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        DiscCopier copier;
        DiscCopyOutcome outcome;
        using (var image = new FileStream(imagePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1 << 20))
        {
            var lastStatus = TimeSpan.Zero;
            copier = new DiscCopier(drive, image, map, m => m.Save(mapPath))
            {
                Note = note => Note(note.Message),
                Progress = new Reporter(status =>
                {
                    if (clock.Elapsed - lastStatus < TimeSpan.FromSeconds(30) && status.Phase is DiscCopyPhase.FirstPass or DiscCopyPhase.Sweep)
                    {
                        return;
                    }

                    lastStatus = clock.Elapsed;
                    Note(string.Create(CultureInfo.InvariantCulture, $"  {status.Phase}, pass {status.Sweep + 1}{(status.Backwards ? " backwards" : string.Empty)}, at sector {status.Sector} in pieces of {status.PieceSectors}: {Mb(status.ReadSectors)} read, {Mb(status.MissingSectors)} missing, {Mb(status.UntriedSectors)} untried, {status.BytesPerSecond / 1_000_000:0.0} MB/s, {status.Failures} failed reads{(status.Stalled ? ", STALLED" : string.Empty)}"));
                }),
            };
            outcome = copier.RunAsync(stop.Token).GetAwaiter().GetResult();

            Note($"{outcome}: {Mb(map.ReadSectors)} read, {Mb(map.Sectors - map.ReadSectors)} not; {copier.Failures} failed reads, rested {copier.Rested.TotalMinutes:0.0} min.");
            if (outcome != DiscCopyOutcome.Complete)
            {
                var missing = map.Ranges.Where(r => !r.State.IsRead()).ToList();
                foreach (var range in missing.Take(20))
                {
                    Console.WriteLine($"  {range.State}: sectors {range.Start}-{range.End - 1} ({Mb(range.Count)}), {DiscGeometry.RadiusMm(range.Start, drive.Media, drive.Layer0Sectors) / 10:0.0} cm from the centre");
                }

                var isCd = drive.Media == DiscMedia.Cd;
                var files = IsoFileTree.Read(
                    (s, destination) =>
                    {
                        // A CD copy is raw; its file names are left out here.
                        if (isCd || !map.StateAt(s).IsRead())
                        {
                            return false;
                        }

                        image.Position = s * DiscSectors.Size;
                        image.ReadExactly(destination);
                        return true;
                    },
                    map.Sectors);
                var (shares, outside) = IsoFileTree.FilesIn(files, missing.Select(r => (r.Start, r.Count)));
                foreach (var share in shares.Take(10))
                {
                    Console.WriteLine($"  {Mb(share.Sectors)} of {share.File.Path} ({Mb(share.File.Sectors)})");
                }

                if (outside > 0)
                {
                    Console.WriteLine($"  {Mb(outside)} in no file{(files.Complete ? string.Empty : " or in folders not read yet")}");
                }

                Console.WriteLine("Run the same command again to go on from here.");
                return 1;
            }
        }

        return RedumpCheckAsync([imagePath]).GetAwaiter().GetResult() == 0 ? 0 : 1;
    }

    /// <summary>Hashes an image and checks it against Redump's PS2 list, kept in the speed test's temp folder.</summary>
    public static async Task<int> RedumpCheckAsync(string[] options)
    {
        var imagePath = Path.GetFullPath(options[0]);
        var refresh = options.Length == 2 && options[1] == "--refresh";
        if (options.Length > 2 || (options.Length == 2 && !refresh) || !File.Exists(imagePath))
        {
            return 2;
        }

        using var http = new HttpClient();
        var cache = new RedumpDatCache(http, Path.Combine(Path.GetTempPath(), "Loadout.SpeedTest", "redump"), "speedtest");
        var list = await cache.GetAsync(refresh, CancellationToken.None);
        if (list.Failure is not null)
        {
            Console.WriteLine(list.Failure);
        }

        var clock = Stopwatch.StartNew();
        DiscImageHashes hashes;
        await using (var image = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true))
        {
            hashes = await DiscImageHashes.ComputeAsync(image, null, CancellationToken.None);
        }

        var check = list.Dat?.Check(hashes.Size, hashes.Sha1);
        Console.Write(hashes.Format(Path.GetFileName(imagePath), check, list.Dat?.Version));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Hashed in {clock.Elapsed.TotalSeconds:0.0} s{(list.Downloaded ? "; Redump's list was downloaded" : string.Empty)}."));
        if (check?.Verdict == RedumpVerdict.Matches)
        {
            var extension = imagePath.EndsWith(DiscBackupFiles.CdImageExtension, StringComparison.OrdinalIgnoreCase) ? DiscBackupFiles.CdImageExtension : DiscBackupFiles.ImageExtension;
            Console.WriteLine($"Named as Redump names it: {DiscBackupFiles.NameFor(check.Game!)}{extension}");
        }

        return check?.Verdict == RedumpVerdict.Matches ? 0 : 1;
    }

    /// <summary>
    /// Reads a CD whole as raw 2352-byte sectors (READ CD through pass-through), as Redump keeps a CD in its .bin, then
    /// checks the file against Redump's list. A trial of the CD backup: no passes, it stops at the first failed read.
    /// </summary>
    public static async Task<int> CdRawAsync(string driveName, string[] options)
    {
        if (options.Length != 1 || !OperatingSystem.IsWindows())
        {
            return 2;
        }

        var path = Path.GetFullPath(options[0]);
        using var drive = WindowsDiscDrive.Open(driveName);
        Console.WriteLine($"{drive.Name}: {drive.Media}, {drive.Sectors} sectors; reading them raw to {path}");
        const int Piece = 108;
        var buffer = new byte[Piece * WindowsDiscDrive.RawCdSectorSize];
        var clock = Stopwatch.StartNew();
        var nextReport = 0L;
        await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20))
        {
            for (var sector = 0L; sector < drive.Sectors; sector += Piece)
            {
                var count = (int)Math.Min(Piece, drive.Sectors - sector);
                var data = buffer.AsMemory(0, count * WindowsDiscDrive.RawCdSectorSize);
                if (!drive.TryReadRawCd(sector, data.Span, out var failure))
                {
                    Console.Error.WriteLine($"Sector {sector} could not be read raw: {failure}");
                    return 1;
                }

                await file.WriteAsync(data);
                if (sector >= nextReport)
                {
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {100.0 * sector / drive.Sectors:0}% at {clock.Elapsed.TotalSeconds:0} s"));
                    nextReport += drive.Sectors / 10;
                }
            }
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Read in {clock.Elapsed.TotalSeconds:0} s, {drive.Sectors * WindowsDiscDrive.RawCdSectorSize / 1_000_000.0 / clock.Elapsed.TotalSeconds:0.0} MB/s."));
        return await RedumpCheckAsync([path]);
    }

    /// <summary>
    /// Shows what the drive says about its read speed: GET PERFORMANCE (nominal, per stretch of the disc in it) and the
    /// capabilities page (MODE SENSE page 2A: maximum and current read speed). Speeds are in kB/s (1000 bytes): CD 1x is
    /// 176.4 kB/s of raw sectors (150 of data), DVD 1x is 1385 kB/s.
    /// </summary>
    public static int Speed(string driveName)
    {
        using var drive = DiscDrive.Open(driveName);
        var x = drive.IsCd ? 176.4 : 1385.0;
        var unit = drive.IsCd ? "CD" : "DVD";
        Console.WriteLine($"{drive.Name}: {(drive.IsCd ? "CD" : "DVD")}, {drive.Length / DiscDrive.SectorSize} sectors.");

        // GET PERFORMANCE, type 0 (performance), nominal read figures, from LBA 0, up to 16 descriptors.
        var cdb = new byte[12];
        cdb[0] = 0xAC;
        cdb[1] = 0x10; // tolerance 10b, as MMC requires for performance data; read, nominal
        cdb[9] = 16;
        var data = new byte[8 + (16 * 16)];
        var error = drive.Scsi(cdb, data, dataIn: true, 30, out var status, out var sense);
        if (error is not null || status != 0)
        {
            Console.WriteLine($"GET PERFORMANCE: {error ?? $"status 0x{status:X2}, sense {sense[2] & 0x0F:X}/{sense[12]:X2}/{sense[13]:X2}"}");
        }
        else
        {
            var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data);
            Console.WriteLine($"GET PERFORMANCE: {length} bytes after the length, flags 0x{data[4]:X2}");
            for (var at = 8; at + 16 <= Math.Min(data.Length, length + 4); at += 16)
            {
                var d = data.AsSpan(at);
                var startLba = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(d);
                var startKb = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(d[4..]);
                var endLba = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(d[8..]);
                var endKb = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(d[12..]);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  sectors {startLba}-{endLba}: {startKb} kB/s ({startKb / x:0.0}x {unit}) to {endKb} kB/s ({endKb / x:0.0}x)"));
            }
        }

        // MODE SENSE (10), page 2A, no block descriptors.
        cdb = new byte[10];
        cdb[0] = 0x5A;
        cdb[1] = 0x08;
        cdb[2] = 0x2A;
        cdb[7] = 1;
        data = new byte[256];
        error = drive.Scsi(cdb, data, dataIn: true, 30, out status, out sense);
        if (error is not null || status != 0)
        {
            Console.WriteLine($"MODE SENSE 2A: {error ?? $"status 0x{status:X2}, sense {sense[2] & 0x0F:X}/{sense[12]:X2}/{sense[13]:X2}"}");
            return 1;
        }

        var page = data[8..];
        int Word(int offset) => (page[offset] << 8) | page[offset + 1];
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"MODE SENSE 2A (page 0x{page[0] & 0x3F:X2}, {page[1]} bytes): max read {Word(8)} kB/s ({Word(8) / x:0.0}x), current read {Word(14)} kB/s ({Word(14) / x:0.0}x), max write {Word(18)} kB/s, current write {Word(28)} kB/s"));
        return 0;
    }

    private static string Mb(long sectors) => string.Create(CultureInfo.InvariantCulture, $"{sectors * DiscSectors.Size / 1_000_000.0:0.0} MB");

    /// <summary>Hands each status on as it comes, on the copier's thread.</summary>
    private sealed class Reporter(Action<DiscCopyStatus> report) : IProgress<DiscCopyStatus>
    {
        public void Report(DiscCopyStatus value) => report(value);
    }

    /// <summary>
    /// An experiment: can the drive be told to give up on a bad sector quickly? Shows the drive's read retry count (mode
    /// page 01h), sets it for this power-on only when asked, times raw READ(10)s of the sector and of sector 16, and puts
    /// the count back.
    /// </summary>
    public static int FastFail(string driveName, string[] options)
    {
        if (options.Length is not (1 or 3 or 5))
        {
            return 2;
        }

        var target = long.Parse(options[0], CultureInfo.InvariantCulture);
        int? retries = null;
        var timeout = 10;
        for (var i = 1; i + 1 < options.Length; i += 2)
        {
            switch (options[i])
            {
                case "--retries": retries = int.Parse(options[i + 1], CultureInfo.InvariantCulture); break;
                case "--timeout": timeout = int.Parse(options[i + 1], CultureInfo.InvariantCulture); break;
                default: return 2;
            }
        }

        using var drive = DiscDrive.Open(driveName);
        var clock = Stopwatch.StartNew();
        void Note(string line) => Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[{clock.Elapsed.TotalSeconds,6:0.0} s] {line}"));
        static string Sense(byte status, byte[] sense) => sense.Length < 14
            ? $"status 0x{status:X2}"
            : $"status 0x{status:X2}, sense key 0x{sense[2] & 0x0F:X}, ASC/ASCQ 0x{sense[12]:X2}/0x{sense[13]:X2}";

        byte[]? ReadPage()
        {
            var data = new byte[64];
            var error = drive.Scsi([0x5A, 0x08, 0x01, 0, 0, 0, 0, 0, 64, 0], data, true, 5, out var status, out var sense);
            Note(error is null && status == 0
                ? $"error recovery page: flags 0x{data[10]:X2}, read retry count {data[11]} (page length {data[9]})"
                : $"MODE SENSE page 01h failed: {error ?? Sense(status, sense)}");
            return error is null && status == 0 && data[8] == 0x01 ? data : null;
        }

        bool WritePage(byte[] page, byte count)
        {
            var length = 8 + page[9] + 2;
            var data = page[..length];
            data[0] = 0;
            data[1] = 0;
            data[8] &= 0x3F;
            data[11] = count;
            var error = drive.Scsi([0x55, 0x10, 0, 0, 0, 0, 0, (byte)(length >> 8), (byte)length, 0], data, false, 5, out var status, out var sense);
            Note(error is null && status == 0 ? $"read retry count set to {count}" : $"MODE SELECT failed: {error ?? Sense(status, sense)}");
            return error is null && status == 0;
        }

        void Read(long sector)
        {
            var data = new byte[DiscDrive.SectorSize];
            var started = clock.Elapsed;
            var error = drive.Scsi([0x28, 0, (byte)(sector >> 24), (byte)(sector >> 16), (byte)(sector >> 8), (byte)sector, 0, 0, 1, 0], data, true, timeout, out var status, out var sense);
            Note(string.Create(CultureInfo.InvariantCulture, $"READ(10) sector {sector}: {(error is null ? Sense(status, sense) : "not sent: " + error)} in {(clock.Elapsed - started).TotalSeconds:0.0} s"));
        }

        var page = ReadPage();
        var original = page?[11];
        if (retries is { } count && page is not null && WritePage(page, (byte)count))
        {
            _ = ReadPage();
        }

        Read(16);
        Read(target);
        Read(16);
        Read(target + 1);
        Read(16);
        if (retries is not null && page is not null && original is { } restore)
        {
            _ = WritePage(page, restore);
        }

        return 0;
    }

    /// <summary>The disc in the drive, ready to send to Loadout Bay. Prints why and returns null when it cannot be.</summary>
    public static GameImageSource? Source(string driveName, string? title)
    {
        var drive = DiscDrive.Open(driveName);
        ImageFacts facts;
        using (var probe = new DiscDriveStream(drive, drive.Length))
        {
            facts = Iso9660Image.Read(probe, drive.Name, SectorLayout.Headerless2048, drive.IsCd);
        }

        if (!facts.Readable || facts.DiscId is not { } discId)
        {
            Console.Error.WriteLine($"The disc in {drive.Name} cannot be installed: {facts.Failure ?? "no disc ID"}.");
            drive.Dispose();
            return null;
        }

        var request = new BayInstallRequest(
            discId.Canonical,
            discId.StartupFileName,
            title ?? facts.VolumeLabel ?? discId.Canonical,
            facts.Layer0Sectors,
            facts.Layer1Sectors,
            facts.Media is MediaKind.Cd ? HdlDiscTypes.Ps2Cd : HdlDiscTypes.Ps2Dvd);
        var started = Stopwatch.StartNew();
        return new GameImageSource(request, () =>
        {
            var stream = new DiscDriveStream(drive, request.TotalBytes);
            stream.Log.Event = line => Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  [{started.Elapsed.TotalSeconds,7:0.0} s] {line}"));
            return stream;
        });
    }

    private static string ProfileName(int profile) => profile switch
    {
        0x08 => "CD-ROM",
        0x09 => "CD-R",
        0x0A => "CD-RW",
        0x10 => "DVD-ROM",
        0x11 => "DVD-R",
        0x1A => "DVD+RW",
        0x1B => "DVD+R",
        0x2B => "DVD+R DL",
        _ => "other",
    };
}
