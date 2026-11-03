using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Loadout.Bay;

/// <summary>A failure Loadout Bay reported, or a reply that was not Loadout Bay's.</summary>
public sealed class LoadoutBayException : Exception
{
    public LoadoutBayException(string message, int status = 0, Exception? inner = null)
        : base(message, inner)
    {
        Status = status;
    }

    /// <summary>Loadout Bay's status (a negative errno value), or 0 when the failure was on the PC's side.</summary>
    public int Status { get; }

    /// <summary>True when nothing took the connection: refused, unreachable, or no answer in time.</summary>
    public bool Unreachable { get; init; }
}

/// <summary>
/// One connection to Loadout Bay's control port. <see cref="ConnectAsync"/> connects and greets;
/// a connection that does not get past the greeting is closed again.
/// </summary>
public sealed class LoadoutBayClient : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(1);

    private const int SendBufferBytes = 1024 * 1024;

    private readonly TcpClient _client;

    private readonly NetworkStream _stream;

    private LoadoutBayClient(TcpClient client, BayHello hello)
    {
        _client = client;
        _stream = client.GetStream();
        Hello = hello;
    }

    public BayHello Hello { get; }

    /// <summary>
    /// For testing the check on a real Loadout Bay: the checksum of this MiB goes out wrong, so the
    /// check must refuse the copy. Never set by the app.
    /// </summary>
    internal int? SpoilChecksumAt { get; set; }

    public static Task<LoadoutBayClient> ConnectAsync(string host, int port = BayWire.Port, CancellationToken cancellationToken = default) =>
        ConnectAsync(host, port, anyVersion: false, cancellationToken);

    /// <summary>
    /// Connects for sending a new Loadout Bay only. The greeting may name any protocol version from 5
    /// on, since ELF_BEGIN, ELF_DATA, and ELF_RUN are the same in all of them.
    /// </summary>
    public static Task<LoadoutBayClient> ConnectForUpgradeAsync(string host, int port = BayWire.Port, CancellationToken cancellationToken = default) =>
        ConnectAsync(host, port, anyVersion: true, cancellationToken);

    private static async Task<LoadoutBayClient> ConnectAsync(string host, int port, bool anyVersion, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var client = new TcpClient { NoDelay = true };
        try
        {
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connect.CancelAfter(ConnectTimeout);
                try
                {
                    await client.ConnectAsync(host, port, connect.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new LoadoutBayException(Unreachable(host, port, "it did not answer within 5 seconds")) { Unreachable = true };
                }
                catch (SocketException ex)
                {
                    throw new LoadoutBayException(Unreachable(host, port, ex.Message), inner: ex) { Unreachable = true };
                }
            }

            var request = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(request, BayWire.ProtocolVersion);
            var (status, payload) = await ExchangeAsync(client.GetStream(), BayWire.CommandHello, request, cancellationToken).ConfigureAwait(false);
            if (anyVersion && status == BayWire.StatusProtocolMismatch && payload.Length >= BayWire.HelloBaseBytes)
            {
                return new LoadoutBayClient(client, BayWire.ReadHello(payload));
            }

            if (status == BayWire.StatusProtocolMismatch && payload.Length >= BayWire.HelloBaseBytes)
            {
                var theirs = BayWire.ReadHello(payload);
                throw new LoadoutBayException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Loadout Bay {theirs.Version} speaks protocol {theirs.ProtocolVersion}, and this Loadout speaks protocol {BayWire.ProtocolVersion}. Use Loadout and Loadout Bay from the same release."),
                    status);
            }

            ThrowIfFailed(status);
            var hello = BayWire.ReadHello(payload);
            if (hello.ProtocolVersion != BayWire.ProtocolVersion)
            {
                throw new LoadoutBayException("Loadout Bay accepted the greeting but named another protocol version.");
            }

            return new LoadoutBayClient(client, hello);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task<BayFreeSpace> GetFreeSpaceAsync(CancellationToken cancellationToken = default)
    {
        var (status, payload) = await ExchangeAsync(_stream, BayWire.CommandFreeSpace, [], cancellationToken).ConfigureAwait(false);
        ThrowIfFailed(status);
        return BayWire.ReadFreeSpace(payload);
    }

    /// <summary>
    /// Every game partition on the PS2, in the disk's partition order, read a page at a time.
    /// The list is refused if the number of games changes while it is read.
    /// </summary>
    public async Task<IReadOnlyList<BayGame>> ListGamesAsync(CancellationToken cancellationToken = default)
    {
        var games = new List<BayGame>();
        var request = new byte[4];
        int? total = null;
        while (total is null || games.Count < total)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(request, (uint)games.Count);
            var (status, payload) = await ExchangeAsync(_stream, BayWire.CommandListGames, request, cancellationToken).ConfigureAwait(false);
            ThrowIfFailed(status);
            var page = ReadPage(payload);
            if ((total is not null && page.Total != total) || (page.Games.Count == 0 && games.Count < page.Total))
            {
                throw new LoadoutBayException("The games on the PS2 changed while the list was read. Refresh again.");
            }

            total = page.Total;
            games.AddRange(page.Games);
        }

        return games;
    }

    /// <summary>One game partition by name.</summary>
    public async Task<BayGame> ReadGameAsync(string partitionName, CancellationToken cancellationToken = default)
    {
        var (status, payload) = await ExchangeAsync(_stream, BayWire.CommandReadEntry, NameRequest(partitionName), cancellationToken).ConfigureAwait(false);
        ThrowIfFailed(status);
        return ReadEntry(payload);
    }

    /// <summary>
    /// Removes an incomplete install. Loadout Bay reads the partition's header again and removes
    /// it only when it is a game's name with a header area of zeros; otherwise nothing changes.
    /// </summary>
    public async Task RemoveIncompleteAsync(string partitionName, CancellationToken cancellationToken = default)
    {
        var (status, _) = await ExchangeAsync(_stream, BayWire.CommandRemoveIncomplete, NameRequest(partitionName), cancellationToken).ConfigureAwait(false);
        ThrowIfFailed(status);
    }

    /// <summary>
    /// Deletes a whole game with its sub-partitions. Loadout Bay reads the partition's header again
    /// and deletes it only when it is a game's name with an HDLoader header; otherwise nothing changes.
    /// The game's artwork and settings in <c>+OPL</c> stay.
    /// </summary>
    public async Task DeleteGameAsync(string partitionName, CancellationToken cancellationToken = default)
    {
        var (status, _) = await ExchangeAsync(_stream, BayWire.CommandDeleteGame, NameRequest(partitionName), cancellationToken).ConfigureAwait(false);
        if (status == BayWire.StatusNotPermitted)
        {
            throw new LoadoutBayException("That partition is not a whole game named like one, so Loadout Bay left it alone.", status);
        }

        ThrowIfFailed(status);
    }

    /// <summary>
    /// Rewrites a game's title, compatibility modes, transfer mode, and disc type. Loadout Bay keeps
    /// every other byte of the header and returns the game as its disk now holds it.
    /// </summary>
    public async Task<BayGame> UpdateGameAsync(string partitionName, BayGameEdit edit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        _ = NameRequest(partitionName);
        if (string.IsNullOrWhiteSpace(edit.Title) || edit.Title.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A title needs at least one character and no NUL.", nameof(edit));
        }

        var (status, payload) = await ExchangeAsync(_stream, BayWire.CommandUpdateGame, BayWire.WriteUpdateGame(partitionName, edit), cancellationToken).ConfigureAwait(false);
        if (status == BayWire.StatusNotPermitted)
        {
            throw new LoadoutBayException("That partition is not a whole game named like one, so Loadout Bay left it alone.", status);
        }

        ThrowIfFailed(status);
        return ReadEntry(payload);
    }

    /// <summary>Every file in one of OPL's folders, read a page at a time. A folder OPL has not made yet is empty.</summary>
    public async Task<IReadOnlyList<BayFile>> ListFilesAsync(BayFolder folder, CancellationToken cancellationToken = default)
    {
        var files = new List<BayFile>();
        var request = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(request, (uint)folder);
        int? total = null;
        while (total is null || files.Count < total)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), (uint)files.Count);
            var (status, payload) = await ExchangeAsync(_stream, BayWire.CommandFileList, request, cancellationToken).ConfigureAwait(false);
            ThrowIfFileFailed(status, folder, null);
            (int Total, IReadOnlyList<BayFile> Files) page;
            try
            {
                page = BayWire.ReadFilePage(payload);
            }
            catch (InvalidDataException ex)
            {
                throw new LoadoutBayException(ex.Message, inner: ex);
            }

            if ((total is not null && page.Total != total) || (page.Files.Count == 0 && files.Count < page.Total))
            {
                throw new LoadoutBayException("The files on the PS2 changed while they were listed. Try again.");
            }

            total = page.Total;
            files.AddRange(page.Files);
        }

        return files;
    }

    /// <summary>A whole file from one of OPL's folders, read in chunks.</summary>
    public async Task<byte[]> ReadFileAsync(BayFolder folder, string name, CancellationToken cancellationToken = default)
    {
        using var content = new MemoryStream();
        while (true)
        {
            var request = BayWire.WriteFileRequest(folder, name, 8);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(BayWire.FileFieldsBytes), (uint)content.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(BayWire.FileFieldsBytes + 4), BayWire.FileReadChunkBytes);
            var (status, payload) = await ExchangeAsync(_stream, BayWire.CommandFileRead, request, cancellationToken).ConfigureAwait(false);
            ThrowIfFileFailed(status, folder, name);
            content.Write(payload);
            if (payload.Length < BayWire.FileReadChunkBytes)
            {
                return content.ToArray();
            }
        }
    }

    /// <summary>
    /// Writes a whole file into one of OPL's folders. The chunks go into <c>&lt;name&gt;.part</c>, which
    /// replaces the file only once all of it is there, so a transfer cut short leaves the old file whole.
    /// <paramref name="progress"/> gets the bytes sent so far.
    /// </summary>
    public async Task WriteFileAsync(
        BayFolder folder, string name, ReadOnlyMemory<byte> content, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        if (content.IsEmpty)
        {
            throw new ArgumentException("Loadout Bay does not write empty files.", nameof(content));
        }

        for (var offset = 0; offset < content.Length;)
        {
            var chunk = content.Slice(offset, Math.Min(BayWire.FileChunkMaxBytes, content.Length - offset));
            var request = BayWire.WriteFileRequest(folder, name, 4 + chunk.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(BayWire.FileFieldsBytes), (uint)offset);
            chunk.Span.CopyTo(request.AsSpan(BayWire.FileFieldsBytes + 4));
            var (status, _) = await ExchangeAsync(_stream, BayWire.CommandFileWrite, request, cancellationToken).ConfigureAwait(false);
            ThrowIfFileFailed(status, folder, name);
            offset += chunk.Length;
            progress?.Report(offset);
        }

        var commit = BayWire.WriteFileRequest(folder, name, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(commit.AsSpan(BayWire.FileFieldsBytes), (uint)content.Length);
        var (commitStatus, _) = await ExchangeAsync(_stream, BayWire.CommandFileCommit, commit, cancellationToken).ConfigureAwait(false);
        ThrowIfFileFailed(commitStatus, folder, name);
    }

    /// <summary>
    /// The block runs a file's PFS inode lists, its own block included. OPL refuses a VMC as
    /// fragmented when this is <see cref="BayWire.VmcMaxRuns"/> or more.
    /// </summary>
    public async Task<int> GetFileRunsAsync(BayFolder folder, string name, CancellationToken cancellationToken = default)
    {
        var (status, payload) = await ExchangeAsync(_stream, BayWire.CommandFileRuns, BayWire.WriteFileRequest(folder, name, 0), cancellationToken).ConfigureAwait(false);
        ThrowIfFileFailed(status, folder, name);
        if (payload.Length != 4)
        {
            throw new LoadoutBayException("Loadout Bay's answer about " + name + " has the wrong size.");
        }

        return (int)BinaryPrimitives.ReadUInt32LittleEndian(payload);
    }

    /// <summary>Sends a new Loadout Bay ELF, which waits in memory for <see cref="RunElfAsync"/> or <see cref="SaveElfAsync"/>.</summary>
    public async Task SendElfAsync(ReadOnlyMemory<byte> elf, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        if (elf.IsEmpty || elf.Length > BayWire.ElfMaxBytes)
        {
            throw new ArgumentException("A Loadout Bay ELF is 1 byte to 8 MiB.", nameof(elf));
        }

        var begin = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(begin, (uint)elf.Length);
        var (beginStatus, _) = await ExchangeAsync(_stream, BayWire.CommandElfBegin, begin, cancellationToken).ConfigureAwait(false);
        ThrowIfElfFailed(beginStatus);
        for (var offset = 0; offset < elf.Length;)
        {
            var chunk = elf.Slice(offset, Math.Min(BayWire.FileChunkMaxBytes, elf.Length - offset));
            var request = new byte[4 + chunk.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(request, (uint)offset);
            chunk.Span.CopyTo(request.AsSpan(4));
            var (status, _) = await ExchangeAsync(_stream, BayWire.CommandElfData, request, cancellationToken).ConfigureAwait(false);
            ThrowIfElfFailed(status);
            offset += chunk.Length;
            progress?.Report(offset);
        }
    }

    /// <summary>
    /// Starts the ELF sent with <see cref="SendElfAsync"/> in place of the running Loadout Bay, which
    /// closes this connection. The new copy names <paramref name="tag"/> in its greeting.
    /// </summary>
    public async Task RunElfAsync(string tag, CancellationToken cancellationToken = default)
    {
        if (tag.Length is 0 or > BayWire.RunTagBytes || !tag.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            throw new ArgumentException("A run tag is 1 to 16 letters, digits, or dashes.", nameof(tag));
        }

        var request = new byte[BayWire.RunTagBytes];
        Encoding.ASCII.GetBytes(tag, request);
        var (status, _) = await ExchangeAsync(_stream, BayWire.CommandElfRun, request, cancellationToken).ConfigureAwait(false);
        ThrowIfElfFailed(status);
    }

    /// <summary>Writes the ELF sent with <see cref="SendElfAsync"/> over the file the running Loadout Bay was started from.</summary>
    public async Task SaveElfAsync(CancellationToken cancellationToken = default)
    {
        var (status, _) = await ExchangeAsync(_stream, BayWire.CommandElfSave, [], cancellationToken).ConfigureAwait(false);
        ThrowIfElfFailed(status);
    }

    /// <summary>Deletes a file from one of OPL's folders.</summary>
    public async Task DeleteFileAsync(BayFolder folder, string name, CancellationToken cancellationToken = default)
    {
        var (status, _) = await ExchangeAsync(_stream, BayWire.CommandFileDelete, BayWire.WriteFileRequest(folder, name, 0), cancellationToken).ConfigureAwait(false);
        ThrowIfFileFailed(status, folder, name);
    }

    /// <summary>
    /// Reads 512-byte sectors of part <paramref name="part"/> (0 is the main partition) of a game
    /// partition, from <paramref name="sector"/> on, the way the install check reads a game back.
    /// For finding out what the PS2's disk holds; nothing is written.
    /// </summary>
    public async Task<byte[]> ReadSectorsAsync(string partitionName, uint part, uint sector, int count, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(partitionName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, BayWire.ReadSectorsMax);
        var request = new byte[BayWire.EntryNameBytes + 12];
        var name = Encoding.UTF8.GetBytes(partitionName);
        if (name.Length > BayWire.PartitionNameMaxBytes)
        {
            throw new ArgumentException("A partition name has at most 32 bytes.", nameof(partitionName));
        }

        name.CopyTo(request, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(BayWire.EntryNameBytes), part);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(BayWire.EntryNameBytes + 4), sector);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(BayWire.EntryNameBytes + 8), (uint)count);
        var (status, payload) = await ExchangeAsync(_stream, BayWire.CommandReadSectors, request, cancellationToken).ConfigureAwait(false);
        ThrowIfFailed(status);
        return payload;
    }

    /// <summary>
    /// The CRC-32C of <paramref name="count"/> 512-byte sectors of a game partition's part, which
    /// Loadout Bay reads back <paramref name="chunk"/> sectors at a time, the way the install check does.
    /// </summary>
    public async Task<uint> ChecksumSectorsAsync(string partitionName, uint part, uint sector, uint count, uint chunk, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(partitionName);
        var request = new byte[BayWire.EntryNameBytes + 16];
        var name = Encoding.UTF8.GetBytes(partitionName);
        if (name.Length > BayWire.PartitionNameMaxBytes)
        {
            throw new ArgumentException("A partition name has at most 32 bytes.", nameof(partitionName));
        }

        name.CopyTo(request, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(BayWire.EntryNameBytes), part);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(BayWire.EntryNameBytes + 4), sector);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(BayWire.EntryNameBytes + 8), count);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(BayWire.EntryNameBytes + 12), chunk);
        var (status, payload) = await ExchangeAsync(_stream, BayWire.CommandChecksumSectors, request, cancellationToken).ConfigureAwait(false);
        ThrowIfFailed(status);
        if (payload.Length != 4)
        {
            throw new LoadoutBayException("Loadout Bay's checksum reply has the wrong size.");
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(payload);
    }

    /// <summary>
    /// Installs one game. INSTALL_BEGIN creates its partitions, the sectors go to the data port,
    /// and INSTALL_STATUS reports how much is on the PS2's disk while they do. INSTALL_END follows
    /// in every case, and Loadout Bay writes the game's header only when every sector is on its
    /// disk, so a copy that stops early, or is cancelled, is never taken for a whole game.
    /// With <see cref="BayInstallRequest.CheckCopy"/>, a CRC-32C of each MiB follows the data, and
    /// Loadout Bay reads the game back and compares them before it writes the header.
    /// <paramref name="sectors"/> must hold the game's 2048-byte sectors from the first.
    /// </summary>
    public async Task<BayInstallStart> InstallAsync(
        BayInstallRequest game,
        Stream sectors,
        IProgress<BayInstallStatus>? progress = null,
        int dataPort = BayWire.DataPort,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(sectors);
        var (status, payload) = await ExchangeAsync(_stream, BayWire.CommandInstallBegin, BayWire.WriteInstallBegin(game), cancellationToken).ConfigureAwait(false);
        ThrowIfFailed(status);
        BayInstallStart start;
        try
        {
            start = BayWire.ReadInstallBegin(payload);
        }
        catch (InvalidDataException ex)
        {
            throw new LoadoutBayException(ex.Message, inner: ex);
        }

        var cancelled = false;
        var imageEnded = false;
        var checksums = game.CheckCopy ? new List<uint>() : null;
        Exception? dataFailure = null;
        try
        {
            using var data = new TcpClient();
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connect.CancelAfter(ConnectTimeout);
                await data.ConnectAsync(((IPEndPoint)_client.Client.RemoteEndPoint!).Address, dataPort, connect.Token).ConfigureAwait(false);
            }

            using var polling = new CancellationTokenSource();
            var poll = PollInstallAsync(progress, polling.Token);
            try
            {
                imageEnded = !await SendSectorsAsync(data.GetStream(), sectors, game.TotalBytes, checksums, cancellationToken).ConfigureAwait(false);
                if (!imageEnded && checksums is not null)
                {
                    var table = new byte[checksums.Count * 4];
                    for (var i = 0; i < checksums.Count; i++)
                    {
                        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(i * 4), i == SpoilChecksumAt ? ~checksums[i] : checksums[i]);
                    }

                    await data.GetStream().WriteAsync(table, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                await polling.CancelAsync().ConfigureAwait(false);
                await poll.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
            dataFailure = ex;
        }

        // Not cancellable: the install must be closed either way, and a reply cut short would
        // leave the control connection out of step. A check reads the whole game back first, so
        // its reply may take minutes: Loadout Bay is allowed one MiB a second, plus the usual wait.
        var checking = checksums is not null && !cancelled && !imageEnded && dataFailure is null;
        if (checking)
        {
            progress?.Report(new BayInstallStatus((ulong)game.TotalBytes, (ulong)game.TotalBytes, (ulong)game.TotalBytes, Checking: true));
        }

        var endWait = checking ? ReplyTimeout + TimeSpan.FromSeconds(game.TotalBytes / (double)BayWire.CheckBlockBytes) : ReplyTimeout;
        var (endStatus, endPayload) = await ExchangeAsync(_stream, BayWire.CommandInstallEnd, [], CancellationToken.None, endWait).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (cancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (imageEnded)
        {
            throw new LoadoutBayException("The image ended before the last sector it declares, so the game was not installed.", endStatus);
        }

        if (endStatus == BayWire.StatusBrokenPipe && dataFailure is not null)
        {
            throw new LoadoutBayException("The data connection to Loadout Bay was lost, so the game was not installed.", endStatus, dataFailure);
        }

        if (endStatus == BayWire.StatusCopyDiffers && endPayload.Length == 4)
        {
            var block = BinaryPrimitives.ReadUInt32LittleEndian(endPayload);
            throw new LoadoutBayException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The check found that the copy on the PS2 differs from the image at {block} MiB, so the game was not installed. It is left as an incomplete install: remove it and install it again."),
                endStatus);
        }

        ThrowIfFailed(endStatus);
        progress?.Report(new BayInstallStatus((ulong)game.TotalBytes, (ulong)game.TotalBytes, (ulong)game.TotalBytes));
        return start;
    }

    public void Dispose()
    {
        _stream.Dispose();
        _client.Dispose();
    }

    // Returns false when the image ran out before totalBytes. Adds a CRC-32C for each MiB sent to
    // checksums, when given.
    private static async Task<bool> SendSectorsAsync(
        NetworkStream data, Stream sectors, long totalBytes, List<uint>? checksums, CancellationToken cancellationToken)
    {
        var buffer = new byte[SendBufferBytes];
        var crc = Crc32C.Start;
        long inBlock = 0;
        for (long sent = 0; sent < totalBytes;)
        {
            var count = await sectors.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, totalBytes - sent)), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return false;
            }

            if (checksums is not null)
            {
                for (var at = 0; at < count;)
                {
                    var take = (int)Math.Min(count - at, BayWire.CheckBlockBytes - inBlock);
                    crc = Crc32C.Update(crc, buffer.AsSpan(at, take));
                    at += take;
                    inBlock += take;
                    if (inBlock == BayWire.CheckBlockBytes)
                    {
                        checksums.Add(Crc32C.Finish(crc));
                        crc = Crc32C.Start;
                        inBlock = 0;
                    }
                }
            }

            await data.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            sent += count;
        }

        if (checksums is not null && inBlock > 0)
        {
            checksums.Add(Crc32C.Finish(crc));
        }

        return true;
    }

    // Asks for the install's progress once a second until stopped. A request that has started
    // runs to its reply, so the control connection stays in step.
    private async Task PollInstallAsync(IProgress<BayInstallStatus>? progress, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(StatusInterval, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var (_, payload) = await ExchangeAsync(_stream, BayWire.CommandInstallStatus, [], CancellationToken.None).ConfigureAwait(false);
            if (payload.Length == BayWire.InstallStatusReplyBytes)
            {
                progress?.Report(BayWire.ReadInstallStatus(payload));
            }
        }
    }

    private static async Task<(int Status, byte[] Payload)> ExchangeAsync(
        NetworkStream stream, uint command, byte[] request, CancellationToken cancellationToken, TimeSpan? wait = null)
    {
        var timeout = wait ?? ReplyTimeout;
        using var reply = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        reply.CancelAfter(timeout);
        try
        {
            // Header and payload in one write, the way Loadout Bay answers.
            var message = new byte[BayWire.HeaderBytes + request.Length];
            BayWire.WriteHeader(message, command, request.Length);
            request.CopyTo(message, BayWire.HeaderBytes);
            await stream.WriteAsync(message, reply.Token).ConfigureAwait(false);

            var header = new byte[BayWire.HeaderBytes];
            await stream.ReadExactlyAsync(header, reply.Token).ConfigureAwait(false);
            var (status, length) = BayWire.ReadReplyHeader(header);
            var payload = new byte[length];
            await stream.ReadExactlyAsync(payload, reply.Token).ConfigureAwait(false);
            return (status, payload);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LoadoutBayException(string.Create(CultureInfo.InvariantCulture, $"Loadout Bay did not answer within {timeout.TotalSeconds:0} seconds."));
        }
        catch (Exception ex) when (ex is IOException or SocketException or EndOfStreamException)
        {
            throw new LoadoutBayException("The connection to Loadout Bay was lost.", inner: ex);
        }
        catch (InvalidDataException ex)
        {
            throw new LoadoutBayException(ex.Message, inner: ex);
        }
    }

    // A request that is only a partition name, refused here when Loadout Bay would refuse it.
    private static byte[] NameRequest(string partitionName)
    {
        ArgumentException.ThrowIfNullOrEmpty(partitionName);
        var request = Encoding.UTF8.GetBytes(partitionName);
        if (request.Length > BayWire.PartitionNameMaxBytes || partitionName.Contains(',', StringComparison.Ordinal))
        {
            throw new ArgumentException("A partition name has at most 32 bytes and no comma.", nameof(partitionName));
        }

        return request;
    }

    private static BayGame ReadEntry(byte[] payload)
    {
        try
        {
            return BayWire.ReadGame(payload);
        }
        catch (InvalidDataException ex)
        {
            throw new LoadoutBayException(ex.Message, inner: ex);
        }
    }

    private static (int Total, IReadOnlyList<BayGame> Games) ReadPage(byte[] payload)
    {
        try
        {
            return BayWire.ReadGamePage(payload);
        }
        catch (InvalidDataException ex)
        {
            throw new LoadoutBayException(ex.Message, inner: ex);
        }
    }

    // File commands give their own statuses a file's meaning.
    private static void ThrowIfFileFailed(int status, BayFolder folder, string? name)
    {
        var path = folder == BayFolder.App ? "APPS/Loadout-Bay" : folder.ToString().ToUpperInvariant();
        var place = name is null ? "OPL's " + path + " folder" : path + "/" + name;
        switch (status)
        {
            case 0:
                return;
            case BayWire.StatusNotFound:
                throw new LoadoutBayException(
                    name is null
                        ? "Loadout Bay found no OPL partition on the PS2's hard disk. OPL makes it the first time it lists games from the hard disk."
                        : "Loadout Bay found no " + place + " on the PS2, or no OPL partition.",
                    status);
            case BayWire.StatusBadOplPartition:
                throw new LoadoutBayException(
                    "OPL's conf_hdd.cfg names a partition Loadout Bay can't use, so nothing was read or written. A name can only have letters, digits, +, _, . and -.",
                    status);
            case BayWire.StatusNotPermitted:
                throw new LoadoutBayException("Loadout Bay refused the file name " + place + ".", status);
            case BayWire.StatusBusy:
                throw new LoadoutBayException("Loadout Bay is installing a game, so " + place + " has to wait.", status);
            case BayWire.StatusBrokenPipe:
                throw new LoadoutBayException("Part of " + place + " did not arrive, so the file on the PS2 was left as it was.", status);
            case BayWire.StatusExists:
                throw new LoadoutBayException(place + " is already on the PS2, and Loadout Bay does not replace a memory card.", status);
            default:
                ThrowIfFailed(status);
                return;
        }
    }

    private static void ThrowIfElfFailed(int status)
    {
        switch (status)
        {
            case 0:
                return;
            case BayWire.StatusProtocol or BayWire.StatusNotImplemented:
                throw new LoadoutBayException(
                    "This Loadout Bay is older than protocol 5 and cannot take a new version over the network. Copy loadout-bay.elf to its USB drive once.",
                    status);
            case BayWire.StatusNotExecutable:
                throw new LoadoutBayException("Loadout Bay refused the file: it is not an ELF that it can start.", status);
            case BayWire.StatusNoMemory:
                throw new LoadoutBayException("Loadout Bay has no room in memory for the new version.", status);
            case BayWire.StatusNotPermitted:
                throw new LoadoutBayException(
                    "Loadout Bay was not started from a USB drive or the hard disk, so it cannot save the new version where it was started.",
                    status);
            default:
                ThrowIfFailed(status);
                return;
        }
    }

    private static void ThrowIfFailed(int status)
    {
        if (status != 0)
        {
            throw new LoadoutBayException(BayWire.DescribeStatus(status), status);
        }
    }

    private static string Unreachable(string host, int port, string reason) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"Loadout Bay could not be reached at {host}, port {port}: {reason.TrimEnd('.')}. Check that Loadout Bay is running on the PS2 and that the address in Settings matches its screen.");
}
