using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Loadout.Bay;

namespace Loadout.Fakes;

/// <summary>
/// Loadout Bay's control port on loopback, answering the way <c>ps2/src/control.c</c> does:
/// one connection at a time, nothing but HELLO before a matching HELLO.
/// </summary>
public sealed class FakeLoadoutBay : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly TcpListener _dataListener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _serveTask;

    public FakeLoadoutBay(
        uint protocolVersion = BayWire.ProtocolVersion,
        string version = "0.1.0",
        BayFreeSpace? disk = null,
        uint? replyMagic = null,
        IReadOnlyList<(string Partition, byte[] Header)>? games = null)
    {
        Games = games ?? [];
        ProtocolVersion = protocolVersion;
        Version = version;
        Disk = disk;
        ReplyMagic = replyMagic ?? BayWire.Magic;
        _listener.Start();
        _dataListener.Start();
        _serveTask = Task.Run(ServeAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public int DataPort => ((IPEndPoint)_dataListener.LocalEndpoint).Port;

    /// <summary>The INSTALL_BEGIN payloads the fake was sent.</summary>
    public List<byte[]> InstallRequests { get; } = [];

    /// <summary>The bytes the last install's data connection carried.</summary>
    public byte[] Installed => _installed.ToArray();

    /// <summary>INSTALL_BEGIN's status, so a test can refuse an install.</summary>
    public int InstallBeginStatus { get; set; }

    /// <summary>A byte of the installed copy to spoil before a check, as if the disk held something else there.</summary>
    public long? CorruptByteAt { get; set; }

    /// <summary>How many installs passed the check.</summary>
    public int Checked { get; private set; }

    private readonly MemoryStream _installed = new();
    private bool _checking;
    private byte[] _checksums = [];
    private Task? _dataTask;
    private long _installTotal;

    public uint ProtocolVersion { get; }

    public string Version { get; }

    /// <summary>The disk the fake reports, or null for a PS2 without a usable hard disk.</summary>
    public BayFreeSpace? Disk { get; }

    public uint ReplyMagic { get; }

    /// <summary>The game partitions the fake lists, in order, each with its raw 1024-byte header.</summary>
    public IReadOnlyList<(string Partition, byte[] Header)> Games { get; set; }

    /// <summary>Called after each LIST_GAMES page, so a test can change <see cref="Games"/> mid-list.</summary>
    public Action? AfterListPage { get; set; }

    public List<uint> Commands { get; } = [];

    /// <summary>A command, and which of its requests closes the connection unanswered, as if Loadout Bay stopped answering there.</summary>
    public (uint Command, int Request)? HangUpOn { get; set; }

    /// <summary>The files in OPL's folders, by folder and name, including <c>.part</c> files.</summary>
    public Dictionary<(BayFolder Folder, string Name), byte[]> Files { get; } = [];

    /// <summary>False for a disk without OPL's partition: every file command is refused with -ENOENT.</summary>
    public bool HasOplPartition { get; set; } = true;

    /// <summary>True for a <c>conf_hdd.cfg</c> Loadout Bay won't use: every file command is refused with -ENOTDIR.</summary>
    public bool BadOplPartitionName { get; set; }

    /// <summary>What FILE_RUNS answers for a file; 2 (its inode and one run of data) when not set.</summary>
    public Dictionary<(BayFolder Folder, string Name), int> FileRuns { get; } = [];

    /// <summary>The tag the running copy names in its greeting; ELF_RUN sets it, as a restart would.</summary>
    public string RunTag { get; set; } = string.Empty;

    /// <summary>The ELFs ELF_RUN started, in order.</summary>
    public List<byte[]> StartedElfs { get; } = [];

    /// <summary>The file ELF_SAVE wrote, or null.</summary>
    public byte[]? SavedElf { get; private set; }

    /// <summary>False for a copy started from somewhere other than a USB drive or the hard disk: ELF_SAVE is refused with -EPERM.</summary>
    public bool StartedFromUsb { get; set; } = true;

    /// <summary>True for an ELF_RUN that closes the connection but leaves the old copy answering, as if the new one never started.</summary>
    public bool NewCopyNeverStarts { get; set; }

    private byte[]? _elf;
    private int _elfReceived;
    private string? _pendingTag;

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _dataListener.Stop();
        try
        {
            _serveTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Stopping the listener ends the loop with an error. That is the expected way out.
        }

        _stop.Dispose();
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            await ServeSessionAsync(client.GetStream());
        }
    }

    private async Task ServeSessionAsync(NetworkStream stream)
    {
        var greeted = false;
        var helloSeen = false;
        var header = new byte[BayWire.HeaderBytes];
        while (true)
        {
            try
            {
                await stream.ReadExactlyAsync(header, _stop.Token);
            }
            catch (EndOfStreamException)
            {
                return;
            }

            var command = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
            var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != BayWire.Magic || length > BayWire.MaxRequestBytes)
            {
                return;
            }

            var request = new byte[length];
            await stream.ReadExactlyAsync(request, _stop.Token);
            Commands.Add(command);
            if (HangUpOn is { } hangUp && hangUp.Command == command && Commands.Count(seen => seen == command) == hangUp.Request)
            {
                return;
            }

            var (status, payload) = command switch
            {
                _ when command is >= BayWire.CommandElfBegin and <= BayWire.CommandElfRun && helloSeen && !greeted && ProtocolVersion < 5 =>
                    (BayWire.StatusProtocol, []),
                _ when command != BayWire.CommandHello && !greeted && !(helloSeen && command is >= BayWire.CommandElfBegin and <= BayWire.CommandElfRun) =>
                    (BayWire.StatusProtocol, []),
                BayWire.CommandHello => Hello(request),
                BayWire.CommandFreeSpace => FreeSpace(request),
                BayWire.CommandListGames => ListGames(request),
                BayWire.CommandReadEntry => ReadEntry(request),
                BayWire.CommandInstallBegin => InstallBegin(request),
                BayWire.CommandInstallStatus => InstallStatus(),
                BayWire.CommandInstallEnd => await InstallEndAsync(),
                BayWire.CommandRemoveIncomplete => RemoveIncomplete(request),
                BayWire.CommandDeleteGame => DeleteGame(request),
                BayWire.CommandUpdateGame => UpdateGame(request),
                BayWire.CommandFileList => FileList(request),
                BayWire.CommandFileRead => FileRead(request),
                BayWire.CommandFileWrite => FileWrite(request),
                BayWire.CommandFileCommit => FileCommit(request),
                BayWire.CommandFileDelete => FileDelete(request),
                BayWire.CommandElfBegin => ElfBegin(request),
                BayWire.CommandElfData => ElfData(request),
                BayWire.CommandElfRun => ElfRun(request),
                BayWire.CommandElfSave => ElfSave(request),
                BayWire.CommandFileRuns => FileRunsOf(request),
                _ => (BayWire.StatusNotImplemented, Array.Empty<byte>()),
            };
            greeted |= command == BayWire.CommandHello && status == 0;
            helloSeen |= command == BayWire.CommandHello;

            var reply = new byte[BayWire.HeaderBytes + payload.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(reply, ReplyMagic);
            BinaryPrimitives.WriteInt32LittleEndian(reply.AsSpan(4), status);
            BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(8), (uint)payload.Length);
            payload.CopyTo(reply, BayWire.HeaderBytes);
            await stream.WriteAsync(reply, _stop.Token);
            if (_pendingTag is not null)
            {
                // Like Loadout Bay: the connection closes and the new copy answers the next one.
                if (!NewCopyNeverStarts)
                {
                    StartedElfs.Add(_elf!);
                    RunTag = _pendingTag;
                }

                _pendingTag = null;
                return;
            }
        }
    }

    private (int Status, byte[] Payload) Hello(byte[] request)
    {
        if (request.Length != 4)
        {
            return (BayWire.StatusInvalid, []);
        }

        var payload = new byte[ProtocolVersion < 5 ? BayWire.HelloBaseBytes : BayWire.HelloReplyBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, ProtocolVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), Disk is null ? 0 : BayWire.HelloFlagDisk);
        Encoding.UTF8.GetBytes(Version).CopyTo(payload, 8);
        if (ProtocolVersion >= 5)
        {
            Encoding.ASCII.GetBytes(RunTag).CopyTo(payload, BayWire.HelloBaseBytes);
        }

        var matches = BinaryPrimitives.ReadUInt32LittleEndian(request) == ProtocolVersion;
        return (matches ? 0 : BayWire.StatusProtocolMismatch, payload);
    }

    private (int Status, byte[] Payload) ElfBegin(byte[] request)
    {
        _elf = null;
        _elfReceived = 0;
        var size = request.Length == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(request) : 0;
        if (size is 0 or > BayWire.ElfMaxBytes)
        {
            return (BayWire.StatusInvalid, []);
        }

        _elf = new byte[size];
        return (0, []);
    }

    private (int Status, byte[] Payload) ElfData(byte[] request)
    {
        if (_elf is null)
        {
            return (BayWire.StatusNotFound, []);
        }

        if (request.Length <= 4 || BinaryPrimitives.ReadUInt32LittleEndian(request) != _elfReceived || request.Length - 4 > _elf.Length - _elfReceived)
        {
            return (BayWire.StatusInvalid, []);
        }

        request.AsSpan(4).CopyTo(_elf.AsSpan(_elfReceived));
        _elfReceived += request.Length - 4;
        return (0, []);
    }

    // The whole ELF is here and starts with the ELF magic, standing in for ps2sdk's checks.
    private int CheckElf() =>
        _elf is null ? BayWire.StatusNotFound
        : _elfReceived != _elf.Length ? BayWire.StatusInvalid
        : _elf.AsSpan().StartsWith("\u007FELF"u8) ? 0
        : BayWire.StatusNotExecutable;

    private (int Status, byte[] Payload) ElfRun(byte[] request)
    {
        var status = request.Length == BayWire.RunTagBytes ? CheckElf() : BayWire.StatusInvalid;
        if (status == 0)
        {
            _pendingTag = Encoding.ASCII.GetString(request).TrimEnd('\0');
        }

        return (status, []);
    }

    private (int Status, byte[] Payload) ElfSave(byte[] request)
    {
        var status = request.Length == 0 ? CheckElf() : BayWire.StatusInvalid;
        if (status == 0 && !StartedFromUsb)
        {
            status = BayWire.StatusNotPermitted;
        }

        if (status == 0)
        {
            SavedElf = _elf;
        }

        return (status, []);
    }

    private (int Status, byte[] Payload) ListGames(byte[] request)
    {
        if (request.Length != 4)
        {
            return (BayWire.StatusInvalid, []);
        }

        if (Disk is null)
        {
            return (BayWire.StatusNoDevice, []);
        }

        var first = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(request), int.MaxValue);
        var page = Games.Skip(first).Take(BayWire.ListPageGames).ToList();
        var payload = new byte[BayWire.ListReplyHeaderBytes + (page.Count * BayWire.EntryBytes)];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)Games.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), (uint)page.Count);
        for (var i = 0; i < page.Count; i++)
        {
            WriteEntry(payload.AsSpan(BayWire.ListReplyHeaderBytes + (i * BayWire.EntryBytes)), page[i]);
        }

        AfterListPage?.Invoke();
        return (0, payload);
    }

    private (int Status, byte[] Payload) ReadEntry(byte[] request)
    {
        if (request.Length is 0 or > BayWire.PartitionNameMaxBytes)
        {
            return (BayWire.StatusInvalid, []);
        }

        var name = Encoding.UTF8.GetString(request);
        var game = Games.FirstOrDefault(g => g.Partition == name);
        if (game.Partition is null)
        {
            return (BayWire.StatusNotFound, []);
        }

        var payload = new byte[BayWire.EntryBytes];
        WriteEntry(payload, game);
        return (0, payload);
    }

    // Removes a listed game only when its header is all zeros, as Loadout Bay does.
    private (int Status, byte[] Payload) RemoveIncomplete(byte[] request)
    {
        var name = Encoding.UTF8.GetString(request);
        var game = Games.FirstOrDefault(g => g.Partition == name);
        if (game.Partition is null)
        {
            return (BayWire.StatusNotFound, []);
        }

        if (game.Header.AsSpan().ContainsAnyExcept((byte)0) || BayGame.DiscIdOfName(name) is null)
        {
            return (BayWire.StatusNotPermitted, []);
        }

        Games = Games.Where(g => g.Partition != name).ToList();
        return (0, []);
    }

    // Deletes a listed game only when it is a game's name with the HDLoader magic, as Loadout Bay does.
    private (int Status, byte[] Payload) DeleteGame(byte[] request)
    {
        var name = Encoding.UTF8.GetString(request);
        var (status, _) = WholeGame(name);
        if (status == 0)
        {
            Games = Games.Where(g => g.Partition != name).ToList();
        }

        return (status, []);
    }

    // Writes the edit's fields into the listed header and keeps every other byte, as Loadout Bay does.
    private (int Status, byte[] Payload) UpdateGame(byte[] request)
    {
        if (request.Length != BayWire.UpdateGameRequestBytes || request[BayWire.EntryNameBytes - 1] != 0)
        {
            return (BayWire.StatusInvalid, []);
        }

        var name = Encoding.UTF8.GetString(request, 0, BayWire.EntryNameBytes).TrimEnd('\0');
        var (status, header) = WholeGame(name);
        var title = request.AsSpan(BayWire.EntryNameBytes, BayWire.TitleBytes);
        var fields = request.AsSpan(BayWire.EntryNameBytes + BayWire.TitleBytes);
        if (status == 0 && (title[0] == 0 || !title.Contains((byte)0)
            || (fields[3] != BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(236)) && fields[3] is not (0x12 or 0x13 or 0x14))))
        {
            status = BayWire.StatusInvalid;
        }

        if (status != 0)
        {
            return (status, []);
        }

        var edited = header.ToArray();
        title.CopyTo(edited.AsSpan(8));
        edited[169] = fields[0];
        edited[170] = fields[1];
        edited[171] = fields[2];
        BinaryPrimitives.WriteUInt32LittleEndian(edited.AsSpan(236), fields[3]);
        Games = Games.Select(g => g.Partition == name ? (name, edited) : g).ToList();
        var payload = new byte[BayWire.EntryBytes];
        WriteEntry(payload, (name, edited));
        return (0, payload);
    }

    private (int Status, byte[] Payload) FileList(byte[] request)
    {
        if (request.Length != 8)
        {
            return (BayWire.StatusInvalid, []);
        }

        if (BadOplPartitionName)
        {
            return (BayWire.StatusBadOplPartition, []);
        }

        if (!HasOplPartition)
        {
            return (BayWire.StatusNotFound, []);
        }

        var folder = (BayFolder)BinaryPrimitives.ReadUInt32LittleEndian(request);
        var all = Files.Where(f => f.Key.Folder == folder).OrderBy(f => f.Key.Name, StringComparer.Ordinal).ToList();
        var page = all.Skip((int)BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(4))).Take(BayWire.FileListPage).ToList();
        var payload = new byte[8 + (page.Count * BayWire.FileEntryBytes)];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)all.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), (uint)page.Count);
        for (var i = 0; i < page.Count; i++)
        {
            var entry = payload.AsSpan(8 + (i * BayWire.FileEntryBytes));
            Encoding.ASCII.GetBytes(page[i].Key.Name).CopyTo(entry);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[BayWire.FileNameBytes..], (uint)page[i].Value.Length);
        }

        return (0, payload);
    }

    private (int Status, byte[] Payload) FileRead(byte[] request)
    {
        var (status, key) = FileKey(request, 8, exact: true);
        if (status != 0)
        {
            return (status, []);
        }

        if (!Files.TryGetValue(key, out var content))
        {
            return (BayWire.StatusNotFound, []);
        }

        var offset = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(BayWire.FileFieldsBytes)), content.Length);
        var length = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(BayWire.FileFieldsBytes + 4)), BayWire.FileChunkMaxBytes);
        return (0, content.AsSpan(offset, Math.Min(length, content.Length - offset)).ToArray());
    }

    // Chunks go into <name>.part in order; offset 0 starts it again, as Loadout Bay does.
    private (int Status, byte[] Payload) FileWrite(byte[] request)
    {
        var (status, key) = FileKey(request, 5, exact: false);
        if (status != 0)
        {
            return (status, []);
        }

        var part = (key.Folder, key.Name + ".part");
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(BayWire.FileFieldsBytes));
        var held = offset == 0 ? [] : Files.GetValueOrDefault(part, []);
        if (held.Length != offset)
        {
            return (BayWire.StatusInvalid, []);
        }

        Files[part] = [.. held, .. request.AsSpan(BayWire.FileFieldsBytes + 4)];
        return (0, []);
    }

    private (int Status, byte[] Payload) FileCommit(byte[] request)
    {
        var (status, key) = FileKey(request, 4, exact: true);
        if (status != 0)
        {
            return (status, []);
        }

        var part = (key.Folder, key.Name + ".part");
        if (!Files.TryGetValue(part, out var content))
        {
            return (BayWire.StatusNotFound, []);
        }

        if (content.Length != BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(BayWire.FileFieldsBytes)))
        {
            return (BayWire.StatusBrokenPipe, []);
        }

        if (key.Folder == BayFolder.Vmc && Files.ContainsKey(key))
        {
            return (BayWire.StatusExists, []);
        }

        Files.Remove(part);
        Files[key] = content;
        return (0, []);
    }

    private (int Status, byte[] Payload) FileDelete(byte[] request)
    {
        var (status, key) = FileKey(request, 0, exact: true);
        return status != 0 ? (status, []) : Files.Remove(key) ? (0, []) : (BayWire.StatusNotFound, []);
    }

    private (int Status, byte[] Payload) FileRunsOf(byte[] request)
    {
        var (status, key) = FileKey(request, 0, exact: true);
        if (status != 0)
        {
            return (status, []);
        }

        if (!Files.ContainsKey(key))
        {
            return (BayWire.StatusNotFound, []);
        }

        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)FileRuns.GetValueOrDefault(key, 2));
        return (0, payload);
    }

    private (int Status, (BayFolder Folder, string Name) Key) FileKey(byte[] request, int fields, bool exact)
    {
        if ((exact ? request.Length != BayWire.FileFieldsBytes + fields : request.Length < BayWire.FileFieldsBytes + fields)
            || request[BayWire.FileFieldsBytes - 1] != 0)
        {
            return (BayWire.StatusInvalid, default);
        }

        var folder = (BayFolder)BinaryPrimitives.ReadUInt32LittleEndian(request);
        var name = Encoding.ASCII.GetString(request, 4, BayWire.FileNameBytes).TrimEnd('\0');
        if (folder is not (BayFolder.Art or BayFolder.Cfg or BayFolder.Vmc or BayFolder.App))
        {
            return (BayWire.StatusInvalid, default);
        }

        return !BayWire.IsFileName(name) || (folder == BayFolder.Vmc && (name.Length < 5 || !name.EndsWith(".bin", StringComparison.Ordinal)))
            ? (BayWire.StatusNotPermitted, default)
            : !HasOplPartition ? (BayWire.StatusNotFound, default)
            : (0, (folder, name));
    }

    private (int Status, byte[] Header) WholeGame(string name)
    {
        var game = Games.FirstOrDefault(g => g.Partition == name);
        if (game.Partition is null)
        {
            return (BayWire.StatusNotFound, []);
        }

        return HdlGameHeader.TryRead(game.Header) is null || BayGame.DiscIdOfName(name) is null
            ? (BayWire.StatusNotPermitted, [])
            : (0, game.Header);
    }

    private (int Status, byte[] Payload) InstallBegin(byte[] request)
    {
        if (request.Length != BayWire.InstallBeginRequestBytes)
        {
            return (BayWire.StatusInvalid, []);
        }

        InstallRequests.Add(request);
        if (InstallBeginStatus != 0)
        {
            return (InstallBeginStatus, []);
        }

        _installTotal = ((long)BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(236)) + BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(240))) * 2048;
        _checking = (BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(248)) & BayWire.InstallFlagCheck) != 0;
        _checksums = new byte[_checking ? (int)((_installTotal + BayWire.CheckBlockBytes - 1) / BayWire.CheckBlockBytes) * 4 : 0];
        _installed.SetLength(0);
        _dataTask = ReceiveDataAsync();
        var payload = new byte[BayWire.InstallBeginReplyBytes];
        Encoding.UTF8.GetBytes("PP." + Encoding.UTF8.GetString(request, 0, 16).TrimEnd('\0') + ".HDL.FAKE").CopyTo(payload, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(BayWire.EntryNameBytes), 1);
        return (0, payload);
    }

    // Takes one data connection and keeps what it carries, up to the game's size.
    private async Task ReceiveDataAsync()
    {
        using var data = await _dataListener.AcceptTcpClientAsync(_stop.Token);
        var stream = data.GetStream();
        var buffer = new byte[64 * 1024];
        while (_installed.Length < _installTotal)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, _installTotal - _installed.Length)), _stop.Token);
            if (count == 0)
            {
                return;
            }

            lock (_installed)
            {
                _installed.Write(buffer, 0, count);
            }
        }

        // A checked install's checksums follow its last byte.
        await stream.ReadExactlyAsync(_checksums, _stop.Token);
    }

    private (int Status, byte[] Payload) InstallStatus()
    {
        if (_dataTask is null)
        {
            return (BayWire.StatusNotFound, []);
        }

        long received;
        lock (_installed)
        {
            received = _installed.Length;
        }

        var payload = new byte[BayWire.InstallStatusReplyBytes];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, (ulong)_installTotal);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(8), (ulong)received);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(16), (ulong)received);
        return (0, payload);
    }

    private async Task<(int Status, byte[] Payload)> InstallEndAsync()
    {
        if (_dataTask is null)
        {
            return (BayWire.StatusNotFound, []);
        }

        try
        {
            await _dataTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or EndOfStreamException)
        {
            // The data connection broke or never came: the copy is short, as checked below.
        }

        _dataTask = null;
        if (_installed.Length != _installTotal)
        {
            return (BayWire.StatusBrokenPipe, []);
        }

        if (!_checking)
        {
            return (0, []);
        }

        // Checked the way Loadout Bay reads the game back: one CRC-32C per MiB of what it holds.
        var held = _installed.ToArray();
        if (CorruptByteAt is long at)
        {
            held[at] ^= 0xFF;
        }

        for (var block = 0; block * (long)BayWire.CheckBlockBytes < held.Length; block++)
        {
            var start = block * BayWire.CheckBlockBytes;
            var sum = Crc32C.Of(held.AsSpan(start, (int)Math.Min(BayWire.CheckBlockBytes, held.Length - start)));
            if (sum != BinaryPrimitives.ReadUInt32LittleEndian(_checksums.AsSpan(block * 4)))
            {
                var payload = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)block);
                return (BayWire.StatusCopyDiffers, payload);
            }
        }

        Checked++;
        return (0, []);
    }

    private static void WriteEntry(Span<byte> entry, (string Partition, byte[] Header) game)
    {
        Encoding.UTF8.GetBytes(game.Partition).CopyTo(entry);
        game.Header.CopyTo(entry[BayWire.EntryNameBytes..]);
    }

    private (int Status, byte[] Payload) FreeSpace(byte[] request)
    {
        if (request.Length != 0)
        {
            return (BayWire.StatusInvalid, []);
        }

        if (Disk is null)
        {
            return (BayWire.StatusNoDevice, []);
        }

        var payload = new byte[BayWire.FreeSpaceReplyBytes];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, Disk.TotalSectors);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(8), Disk.FreeSectors);
        return (0, payload);
    }
}
