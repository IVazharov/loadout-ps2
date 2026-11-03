using System.Globalization;
using Loadout.Bay;
using Loadout.Images;
using Loadout.SpeedTest;
using Loadout.Vmc;

const string Usage =
    """
    Usage: Loadout.SpeedTest <ps2-address> tcp [--mb <megabytes>] [--disk [--keep] [--unit <KiB>[,<KiB>...]]] [--seed <n>]
           Loadout.SpeedTest <ps2-address> udp [--mb <megabytes>] [--rate <MB/s>[,<MB/s>...]] [--datagram <bytes>]

    Sends test data to Loadout Bay's speed test and prints the PC's figure and Loadout Bay's own.
    1 MB is 1048576 bytes. --mb defaults to 256. --rate 0 sends UDP as fast as the PC can; a list runs one test per rate.
    --disk makes Loadout Bay write the TCP data to a LOADOUT.SPEEDTEST partition on its hard disk and remove it afterwards.
    --keep leaves that partition in place, so the written data can be checked.
    --unit sets the size of each disk write: 16, 32, 64, 128, or 256 KiB. A list runs one disk test per size.
    --seed picks the test data, 1 by default. Use a new seed for each checked run, so old data cannot pass.

           Loadout.SpeedTest verify <disk-image> <partition-start-sector> --mb <megabytes> [--seed <n>]

    Checks a kept disk run in a raw disk image, such as PCSX2's DEV9hdd.raw. The start sector
    is the one PFS Shell lists for LOADOUT.SPEEDTEST, in decimal or 0x hex.

           Loadout.SpeedTest <ps2-address> bay

    Greets Loadout Bay on its control port and prints its version, the hard disk's size and free space,
    and the games on it.

           Loadout.SpeedTest <ps2-address> parts

    Lists each game's parts from its header: the game sectors (2048 bytes) in each part and the
    part's first data sector on the disk (512 bytes).

           Loadout.SpeedTest <ps2-address> install <image> [--title <title>] [--check]

    Installs a game image (iso, chd, or a cue sheet with its bin) on the PS2's hard disk through Loadout Bay.
    The title defaults to the OPL database's title for the disc ID, as on the Install page, or else the
    one read from the image. --check makes Loadout Bay read the game back
    and compare every MiB before it writes the header. --spoil-checksum <MiB> checks too, but sends that
    MiB's checksum wrong, to test that the check refuses the copy.

           Loadout.SpeedTest <ps2-address> read-sectors <partition-name> <part> <first-sector> <count> <file>

    Reads 512-byte sectors of a game partition back into a file, the way the install check reads
    them: part 0 is the main partition, whose data starts at sector 0x2000; a sub-partition's at 4.

           Loadout.SpeedTest <ps2-address> checksum-sectors <partition-name> <part> <first-sector> <count> <sectors-per-read>

    Has Loadout Bay take the CRC-32C of a range of 512-byte sectors of a game partition, read back
    the way the install check reads, the given number of sectors at a time (at most 512).

           Loadout.SpeedTest <ps2-address> remove-incomplete <partition-name>

    Removes a copy that stopped before the end: a game partition whose header area is all zeros.
    Loadout Bay refuses any other partition.

           Loadout.SpeedTest <ps2-address> delete <partition-name>

    Deletes a whole game with its sub-partitions. Loadout Bay refuses a partition without a game header.

           Loadout.SpeedTest <ps2-address> update <partition-name> [--title <title>] [--modes <flags>] [--transfer mdma0|udma4] [--disc-type cd|cdda|dvd]

    Rewrites the named fields of a game's header and keeps every other byte. --modes takes the
    compatibility flags as a number, such as 0x05 for modes 1 and 3. Current OPL reads modes and the
    transfer mode from the game's CFG file instead; only HDLGameInstaller and older loaders read them here.

           Loadout.SpeedTest <ps2-address> bay-update <elf> [--no-save]

    Sends a new Loadout Bay ELF to the running Loadout Bay, which starts it from memory. Once the new
    copy answers, it saves itself over the file it was started from and keeps the old one as .bak,
    unless --no-save is given.

           Loadout.SpeedTest <ps2-address> file list art|cfg|vmc|app
           Loadout.SpeedTest <ps2-address> file get art|cfg|vmc|app <name> <file>
           Loadout.SpeedTest <ps2-address> file put art|cfg|vmc|app <name> <file>
           Loadout.SpeedTest <ps2-address> file rm art|cfg|vmc|app <name>
           Loadout.SpeedTest <ps2-address> file runs art|cfg|vmc|app <name>

    Lists, reads, writes, or deletes a file in the ART, CFG, or VMC folder of OPL's partition. A put
    replaces the file only once all of it is on the PS2, and never replaces a memory card: rm it
    first. runs prints how many block runs the file's inode lists, which OPL checks for a VMC.

           Loadout.SpeedTest verify-game <disk-image> <partition-name> <image>

    Checks an installed game in a raw disk image, such as PCSX2's DEV9hdd.raw: its header, and every
    sector of every part against the game image.

           Loadout.SpeedTest vmc-image 8|16|32|64 <file>

    Writes an empty, formatted virtual memory card the way Loadout makes one for OPL.

           Loadout.SpeedTest find [--probe|--sweep]

    Finds Loadout Bay on the PC's networks, as Settings' Find Bay does: the broadcast probe, then a
    sweep of the local subnets if no Loadout Bay answers. --probe and --sweep run only that one.
    """;

if (args.Length is 1 or 2 && args[0] == "find")
{
    var mode = args.Length == 2 ? args[1] : string.Empty;
    if (mode is not ("" or "--probe" or "--sweep"))
    {
        return UsageError();
    }

    var networks = BayDiscovery.LocalNetworks();
    foreach (var network in networks)
    {
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"This PC: {network.Address}/{network.PrefixLength}"));
    }

    var started = TimeProvider.System.GetTimestamp();
    var found = mode switch
    {
        "--probe" => await BayDiscovery.ProbeAsync(BayDiscovery.ProbeTargets(networks), BayDiscovery.Port, BayDiscovery.ProbeWait),
        "--sweep" => await BayDiscovery.SweepAsync(networks),
        _ => await BayDiscovery.FindAsync(),
    };
    foreach (var bay in found)
    {
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Loadout Bay {bay.Version} at {bay.Address} (protocol {bay.ProtocolVersion}, {(bay.HasHardDisk ? "hard disk" : "no hard disk")})"));
    }

    Console.WriteLine(string.Create(
        CultureInfo.InvariantCulture,
        $"Found {found.Count} in {TimeProvider.System.GetElapsedTime(started).TotalSeconds:0.0} s."));
    return found.Count > 0 ? 0 : 1;
}

if (args.Length < 2)
{
    Console.Error.WriteLine(Usage);
    return 2;
}

if (args[0] == "vmc-image")
{
    if (args.Length != 3 || !int.TryParse(args[1], CultureInfo.InvariantCulture, out var cardSize) || !VmcImage.Sizes.Contains(cardSize))
    {
        return UsageError();
    }

    await File.WriteAllBytesAsync(args[2], VmcImage.Create(cardSize, DateTimeOffset.Now));
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Wrote a {cardSize} MB memory card to {args[2]}."));
    return 0;
}

if (args[0] == "verify")
{
    if ((args.Length != 5 && args.Length != 7) || args[3] != "--mb" || (args.Length == 7 && args[5] != "--seed"))
    {
        return UsageError();
    }

    var checkSeed = args.Length == 7 ? int.Parse(args[6], CultureInfo.InvariantCulture) : SpeedTestClient.DefaultSeed;

    var start = args[2].StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? long.Parse(args[2].AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        : long.Parse(args[2], CultureInfo.InvariantCulture);
    var checkBytes = (long)(double.Parse(args[4], CultureInfo.InvariantCulture) * SpeedTestWire.BytesPerMegabyte);
    var mismatch = DiskImageCheck.FirstMismatch(args[1], start, checkBytes, checkSeed);
    Console.WriteLine(mismatch is long at
        ? string.Create(CultureInfo.InvariantCulture, $"Mismatch at data byte {at} (image byte {(start + DiskImageCheck.DataStartSector) * DiskImageCheck.SectorBytes + at}).")
        : string.Create(CultureInfo.InvariantCulture, $"All {checkBytes} bytes match what the PC sent."));
    return mismatch is null ? 0 : 1;
}

if (args[1] == "bay")
{
    try
    {
        using var bay = await LoadoutBayClient.ConnectAsync(args[0]);
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Loadout Bay {bay.Hello.Version}, protocol {bay.Hello.ProtocolVersion}, hard disk {(bay.Hello.HasHardDisk ? "found" : "not found")}{(bay.Hello.RunTag.Length > 0 ? ", started over the network with tag " + bay.Hello.RunTag : "")}."));
        if (bay.Hello.HasHardDisk)
        {
            var space = await bay.GetFreeSpaceAsync();
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Hard disk: {space.TotalBytes / (double)(1L << 30):F1} GiB, {space.FreeBytes / (double)(1L << 30):F1} GiB free ({space.TotalSectors} and {space.FreeSectors} sectors of 512 bytes)."));

            var games = await bay.ListGamesAsync();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{games.Count} game(s):"));
            foreach (var game in games)
            {
                var row = game.ToInstalledGameRow();
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  {row.PartitionName} | {row.DiscId} | {row.Title} | {row.DiscTypeLabel} | {row.Sectors} sectors | flags 0x{row.CompatibilityModeFlags:X2} | transfer {row.TransferLabel}"));
            }

            if (games.Count > 0)
            {
                var again = await bay.ReadGameAsync(games[0].PartitionName);
                Console.WriteLine(again == games[0]
                    ? "Reading the first game by name gives the same entry."
                    : "Reading the first game by name gives a DIFFERENT entry.");
            }
        }

        return 0;
    }
    catch (LoadoutBayException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

if (args[0] == "verify-game")
{
    if (args.Length != 4)
    {
        return UsageError();
    }

    var game = await GameImageSource.ScanAsync(args[3], null);
    if (game is null)
    {
        return 1;
    }

    using var disk = new FileStream(args[1], FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    var start = GameImageCheck.FindPartition(disk, args[2]);
    if (start is null)
    {
        Console.Error.WriteLine($"No main partition named {args[2]} in {args[1]}.");
        return 1;
    }

    var header = GameImageCheck.ReadHeader(disk, start.Value);
    if (header is null)
    {
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{args[2]} at sector 0x{start:X} has no HDLoader header: the install is incomplete."));
        return 1;
    }

    Console.WriteLine(string.Create(
        CultureInfo.InvariantCulture,
        $"{args[2]} at sector 0x{start:X}: {header.Title} | {header.StartupFileName} | disc type 0x{header.DiscType:X2} | {header.Sectors} sectors | layer 1 at {header.Layer1Start} | flags 0x{header.CompatibilityModeFlags:X2} | transfer 0x{header.TransferType:X2} / {header.TransferMode}"));
    foreach (var part in header.Parts)
    {
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  part at game sector {part.GameSector}: {part.Bytes / 2048} sectors from disk sector 0x{part.DiskSector:X}"));
    }

    if (header.Sectors != game.Request.Layer0Sectors + game.Request.Layer1Sectors)
    {
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"The header holds {header.Sectors} sectors, and the image has {game.Request.Layer0Sectors + game.Request.Layer1Sectors}."));
        return 1;
    }

    using var sectors = game.Open();
    var mismatch = GameImageCheck.FirstMismatch(disk, header, sectors);
    Console.WriteLine(mismatch is long at
        ? string.Create(CultureInfo.InvariantCulture, $"Mismatch at game byte {at} (sector {at / 2048}).")
        : string.Create(CultureInfo.InvariantCulture, $"All {header.Sectors} sectors match the image."));
    return mismatch is null ? 0 : 1;
}

if (args[1] == "install")
{
    string? title = null;
    var check = false;
    int? spoil = null;
    for (var i = 3; i < args.Length; i++)
    {
        if (args[i] == "--title" && i + 1 < args.Length)
        {
            title = args[++i];
        }
        else if (args[i] == "--check")
        {
            check = true;
        }
        else if (args[i] == "--spoil-checksum" && i + 1 < args.Length)
        {
            check = true;
            spoil = int.Parse(args[++i], CultureInfo.InvariantCulture);
        }
        else
        {
            return UsageError();
        }
    }

    var game = await GameImageSource.ScanAsync(args[2], title);
    if (game is null)
    {
        return 1;
    }

    var request = game.Request with { CheckCopy = check };
    Console.WriteLine(string.Create(
        CultureInfo.InvariantCulture,
        $"{request.Title} | {request.DiscId} | {request.StartupFileName} | disc type 0x{request.DiscType:X2} | {request.Layer0Sectors} + {request.Layer1Sectors} sectors"));
    try
    {
        using var bay = await LoadoutBayClient.ConnectAsync(args[0]);
        bay.SpoilChecksumAt = spoil;
        using var sectors = game.Open();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // One line per tenth of the game, so a log of the run stays short.
        var shown = -1;
        var progress = new Progress<BayInstallStatus>(status =>
        {
            lock (clock)
            {
                var tenth = status.Checking ? 10 : (int)(status.WrittenBytes * 10 / Math.Max(status.TotalBytes, 1));
                if (tenth <= shown)
                {
                    return;
                }

                shown = tenth;
                Console.WriteLine(status.Checking ? "  all sent; Loadout Bay is reading it back to check it..." : string.Create(
                    CultureInfo.InvariantCulture,
                    $"  {status.WrittenBytes / SpeedTestWire.BytesPerMegabyte:0} of {status.TotalBytes / SpeedTestWire.BytesPerMegabyte:0} MB on the disk, {status.WrittenBytes / SpeedTestWire.BytesPerMegabyte / Math.Max(clock.Elapsed.TotalSeconds, 0.001):0.00} MB/s"));
            }
        });
        var started = await bay.InstallAsync(request, sectors, progress);
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Installed as {started.PartitionName} in {started.Parts} part(s), {clock.Elapsed.TotalSeconds:0.0} s, {request.TotalBytes / SpeedTestWire.BytesPerMegabyte / clock.Elapsed.TotalSeconds:0.00} MB/s."));
        return 0;
    }
    catch (LoadoutBayException ex)
    {
        Console.WriteLine();
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

if (args[1] == "checksum-sectors")
{
    if (args.Length != 7)
    {
        return UsageError();
    }

    try
    {
        using var bay = await LoadoutBayClient.ConnectAsync(args[0]);
        var part = uint.Parse(args[3], CultureInfo.InvariantCulture);
        var first = args[4].StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.Parse(args[4].AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : uint.Parse(args[4], CultureInfo.InvariantCulture);
        var count = uint.Parse(args[5], CultureInfo.InvariantCulture);
        var chunk = uint.Parse(args[6], CultureInfo.InvariantCulture);
        var crc = await bay.ChecksumSectorsAsync(args[2], part, first, count, chunk);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"CRC-32C {crc:X8} of {count} sectors of part {part} from sector 0x{first:X}, read {chunk} at a time."));
        return 0;
    }
    catch (LoadoutBayException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

if (args[1] == "read-sectors")
{
    if (args.Length != 7)
    {
        return UsageError();
    }

    try
    {
        using var bay = await LoadoutBayClient.ConnectAsync(args[0]);
        var part = uint.Parse(args[3], CultureInfo.InvariantCulture);
        var first = args[4].StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.Parse(args[4].AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : uint.Parse(args[4], CultureInfo.InvariantCulture);
        var count = int.Parse(args[5], CultureInfo.InvariantCulture);
        using var output = File.Create(args[6]);
        for (var done = 0; done < count;)
        {
            var step = Math.Min(BayWire.ReadSectorsMax, count - done);
            output.Write(await bay.ReadSectorsAsync(args[2], part, first + (uint)done, step));
            done += step;
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Read {count} sectors of part {part} from sector 0x{first:X} into {args[6]}."));
        return 0;
    }
    catch (LoadoutBayException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

if (args[1] == "remove-incomplete")
{
    if (args.Length != 3)
    {
        return UsageError();
    }

    try
    {
        using var bay = await LoadoutBayClient.ConnectAsync(args[0]);
        await bay.RemoveIncompleteAsync(args[2]);
        Console.WriteLine($"Removed the incomplete install {args[2]}.");
        return 0;
    }
    catch (LoadoutBayException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

if (args[1] == "file")
{
    BayFolder? folder = args.Length > 3 ? args[3] switch { "art" => BayFolder.Art, "cfg" => BayFolder.Cfg, "vmc" => BayFolder.Vmc, "app" => BayFolder.App, _ => null } : null;
    var folderPath = folder == BayFolder.App ? "APPS/Loadout-Bay" : args.Length > 3 ? args[3].ToUpperInvariant() : "";
    var valid = folder is not null && args[2] switch
    {
        "list" => args.Length == 4,
        "get" or "put" => args.Length == 6,
        "rm" or "runs" => args.Length == 5,
        _ => false,
    };
    if (!valid)
    {
        return UsageError();
    }

    try
    {
        using var bay = await LoadoutBayClient.ConnectAsync(args[0]);
        switch (args[2])
        {
            case "list":
                var files = await bay.ListFilesAsync(folder!.Value);
                foreach (var file in files)
                {
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {file.Name} | {file.Size} bytes"));
                }

                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{files.Count} file(s)."));
                break;
            case "get":
                var content = await bay.ReadFileAsync(folder!.Value, args[4]);
                await File.WriteAllBytesAsync(args[5], content);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Read {content.Length} bytes into {args[5]}."));
                break;
            case "put":
                var bytes = await File.ReadAllBytesAsync(args[5]);
                await bay.WriteFileAsync(folder!.Value, args[4], bytes);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Wrote {bytes.Length} bytes to {folderPath}/{args[4]}."));
                break;
            case "runs":
                var runs = await bay.GetFileRunsAsync(folder!.Value, args[4]);
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{folderPath}/{args[4]}: its inode lists {runs} block run(s); OPL takes a VMC below {BayWire.VmcMaxRuns}."));
                break;
            default:
                await bay.DeleteFileAsync(folder!.Value, args[4]);
                Console.WriteLine($"Deleted {folderPath}/{args[4]}.");
                break;
        }

        return 0;
    }
    catch (LoadoutBayException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

if (args[1] == "delete")
{
    if (args.Length != 3)
    {
        return UsageError();
    }

    try
    {
        using var bay = await LoadoutBayClient.ConnectAsync(args[0]);
        await bay.DeleteGameAsync(args[2]);
        Console.WriteLine($"Deleted {args[2]}.");
        return 0;
    }
    catch (LoadoutBayException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

if (args[1] == "update")
{
    if (args.Length < 5 || args.Length % 2 == 0)
    {
        return UsageError();
    }

    try
    {
        using var bay = await LoadoutBayClient.ConnectAsync(args[0]);
        var header = (await bay.ReadGameAsync(args[2])).Header;
        if (header is null)
        {
            Console.Error.WriteLine($"{args[2]} has no game header to edit.");
            return 1;
        }

        var edit = BayGameEdit.Of(header);
        for (var i = 3; i < args.Length; i += 2)
        {
            var value = args[i + 1];
            edit = args[i] switch
            {
                "--title" => edit with { Title = value },
                "--modes" => edit with { CompatibilityModeFlags = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? byte.Parse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                    : byte.Parse(value, CultureInfo.InvariantCulture) },
                "--transfer" when value == "mdma0" => edit with { TransferType = 0x20, TransferMode = 0 },
                "--transfer" when value == "udma4" => edit with { TransferType = 0x40, TransferMode = 4 },
                "--disc-type" when value == "cd" => edit with { DiscType = HdlDiscTypes.Ps2Cd },
                "--disc-type" when value == "cdda" => edit with { DiscType = HdlDiscTypes.Ps2CdDa },
                "--disc-type" when value == "dvd" => edit with { DiscType = HdlDiscTypes.Ps2Dvd },
                _ => throw new FormatException($"{args[i]} {value} is not an option update takes."),
            };
        }

        var game = await bay.UpdateGameAsync(args[2], edit);
        var row = game.ToInstalledGameRow();
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{row.PartitionName} | {row.DiscId} | {row.Title} | {row.DiscTypeLabel} | {row.Sectors} sectors | flags 0x{row.CompatibilityModeFlags:X2} | transfer {row.TransferLabel} | HDL flags 0x{game.Header!.HdlCompatibilityFlags:X2}"));
        return 0;
    }
    catch (LoadoutBayException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
    catch (FormatException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
}

if (args[1] == "bay-update")
{
    if (args.Length is not (3 or 4) || (args.Length == 4 && args[3] != "--no-save"))
    {
        return UsageError();
    }

    try
    {
        var elf = await File.ReadAllBytesAsync(args[2]);
        var started = DateTime.UtcNow;
        var stages = new Progress<BayUpgradeStage>(stage => Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{(DateTime.UtcNow - started).TotalSeconds,5:0.0} s  {stage}")));
        var result = await BayUpgrade.RunAsync(args[0], elf, save: args.Length == 3, stages);
        Console.WriteLine(result.Saved
            ? $"Loadout Bay {result.Version} runs and is saved where it was started; the old file is its .bak."
            : $"Loadout Bay {result.Version} runs from memory; nothing was saved.");
        return 0;
    }
    catch (LoadoutBayException ex)
    {
        Console.Error.WriteLine(ex.Status == 0 ? ex.Message : string.Create(CultureInfo.InvariantCulture, $"{ex.Message} (status {ex.Status})"));
        return 1;
    }
}

if (args[1] == "parts")
{
    try
    {
        using var bay = await LoadoutBayClient.ConnectAsync(args[0]);
        foreach (var game in await bay.ListGamesAsync())
        {
            var parts = game.Header?.Parts ?? [];
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{game.PartitionName} | {game.Header?.Sectors ?? 0} | {string.Join(" ", parts.Select(part => $"{part.Bytes / 2048}@0x{part.DiskSector:X}"))}"));
        }

        return 0;
    }
    catch (LoadoutBayException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

var host = args[0];
var transport = args[1].ToUpperInvariant();
var megabytes = 256d;
var rates = new List<double> { 0 };
var datagram = SpeedTestWire.MaxDatagramBytes;
var flags = 0u;
var seed = SpeedTestClient.DefaultSeed;
var units = new List<int> { 0 };
for (var i = 2; i < args.Length; i++)
{
    var value = i + 1 < args.Length ? args[i + 1] : null;
    switch (args[i])
    {
        case "--mb" when value is not null:
            megabytes = double.Parse(value, CultureInfo.InvariantCulture);
            i++;
            break;
        case "--rate" when value is not null:
            rates = value.Split(',').Select(rate => double.Parse(rate, CultureInfo.InvariantCulture)).ToList();
            i++;
            break;
        case "--disk":
            flags |= SpeedTestWire.FlagDisk;
            break;
        case "--keep":
            flags |= SpeedTestWire.FlagKeep;
            break;
        case "--unit" when value is not null:
            units = value.Split(',').Select(unit => int.Parse(unit, CultureInfo.InvariantCulture)).ToList();
            i++;
            break;
        case "--seed" when value is not null:
            seed = int.Parse(value, CultureInfo.InvariantCulture);
            i++;
            break;
        case "--datagram" when value is not null:
            datagram = int.Parse(value, CultureInfo.InvariantCulture);
            i++;
            break;
        default:
            return UsageError();
    }
}

var totalBytes = (long)(megabytes * SpeedTestWire.BytesPerMegabyte);
// A wrong argument prints only the usage lines of its command, or every command's usage line when the
// command is not known; running without arguments prints the whole usage.
int UsageError()
{
    var lines = Usage.Split('\n').Select(line => line.Trim()).Where(line => line.Contains("Loadout.SpeedTest ", StringComparison.Ordinal)).ToList();
    var named = lines.Where(line => args.Take(2).Any(arg => line.Contains(" " + arg + " ", StringComparison.Ordinal) || line.EndsWith(" " + arg, StringComparison.Ordinal))).ToList();
    foreach (var line in named.Count > 0 ? named : lines)
    {
        Console.Error.WriteLine(line.Replace("Usage: ", "", StringComparison.Ordinal));
    }

    Console.Error.WriteLine("Run Loadout.SpeedTest without arguments for the whole usage.");
    return 2;
}

void Progress(long sent) => Console.Write(string.Create(CultureInfo.InvariantCulture, $"\r  sent {sent / SpeedTestWire.BytesPerMegabyte:0} MB "));

try
{
    if (transport == "TCP")
    {
        if ((flags & SpeedTestWire.FlagDisk) == 0)
        {
            var result = SpeedTestClient.RunTcp(host, SpeedTestWire.TcpPort, totalBytes, flags, Progress, seed);
            Print(result);
            return result.Status == 0 ? 0 : 1;
        }

        var failed = false;
        foreach (var unit in units)
        {
            Console.WriteLine(unit > 0
                ? string.Create(CultureInfo.InvariantCulture, $"TCP to HDD, {unit} KiB writes:")
                : "TCP to HDD:");
            var diskFlags = SpeedTestWire.DiskFlags((flags & SpeedTestWire.FlagKeep) != 0, unit);
            var result = SpeedTestClient.RunTcp(host, SpeedTestWire.TcpPort, totalBytes, diskFlags, Progress, seed);
            Print(result);
            failed |= result.Status != 0;
        }

        return failed ? 1 : 0;
    }

    if (transport == "UDP")
    {
        foreach (var rate in rates)
        {
            Console.WriteLine(rate > 0
                ? string.Create(CultureInfo.InvariantCulture, $"UDP at {rate:0.0} MB/s:")
                : "UDP as fast as the PC can send:");
            Print(SpeedTestClient.RunUdp(host, SpeedTestWire.UdpPort, totalBytes, rate, datagram, Progress));
        }

        return 0;
    }
}
catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or TimeoutException)
{
    Console.WriteLine();
    Console.Error.WriteLine("The test failed: " + ex.Message);
    return 1;
}

return UsageError();

static void Print(SpeedTestResult result)
{
    Console.WriteLine();
    Console.WriteLine(string.Create(
        CultureInfo.InvariantCulture,
        $"  PC sent      {result.BytesSent / SpeedTestWire.BytesPerMegabyte,8:0.0} MB in {result.PcElapsed.TotalSeconds,6:0.0} s  {result.PcMegabytesPerSecond,6:0.00} MB/s"));
    Console.WriteLine(string.Create(
        CultureInfo.InvariantCulture,
        $"  PS2 received {result.Ps2Bytes / SpeedTestWire.BytesPerMegabyte,8:0.0} MB in {result.Ps2Elapsed.TotalSeconds,6:0.0} s  {result.Ps2MegabytesPerSecond,6:0.00} MB/s"));
    if (result.Status != 0)
    {
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Loadout Bay reported error {result.Status}. The run did not finish on the PS2."));
    }

    if (result.PacketsSent > 0)
    {
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  packets      {result.PacketsReceived} of {result.PacketsSent} arrived, {result.LossPercent:0.00}% lost"));
    }
}
