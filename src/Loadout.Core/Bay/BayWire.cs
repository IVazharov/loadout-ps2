using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Loadout.Core;

namespace Loadout.Bay;

/// <summary>
/// Loadout Bay's control protocol (<c>ps2/src/control.c</c>). Every number is little-endian.
/// <c>docs/bay-protocol.md</c> describes it.
/// </summary>
public static class BayWire
{
    public const int Port = 45060;

    /// <summary>The port a game's sectors are sent to after INSTALL_BEGIN.</summary>
    public const int DataPort = 45061;

    public const uint ProtocolVersion = 6;

    public const uint Magic = 0x3143424C; // "LBC1"

    public const int HeaderBytes = 12;

    /// <summary>A FILE_WRITE carries a 64 KiB chunk after its fields.</summary>
    public const int MaxRequestBytes = FileChunkMaxBytes + 128;

    public const uint CommandHello = 1;

    public const uint CommandFreeSpace = 2;

    /// <summary>The greeting up to the version text, which is all of it before protocol 5.</summary>
    public const int HelloBaseBytes = 24;

    /// <summary>From protocol 5 on, the greeting ends with the run tag the copy was started with.</summary>
    public const int HelloReplyBytes = HelloBaseBytes + RunTagBytes;

    public const uint HelloFlagDisk = 0x1;

    public const int VersionBytes = 16;

    public const int FreeSpaceReplyBytes = 16;

    public const uint CommandListGames = 3;

    public const uint CommandReadEntry = 4;

    /// <summary>The most games one LIST_GAMES reply carries.</summary>
    public const int ListPageGames = 64;

    public const int ListReplyHeaderBytes = 8;

    public const int EntryNameBytes = 40;

    public const int EntryBytes = EntryNameBytes + HdlGameHeader.Bytes;

    /// <summary>The longest APA partition name.</summary>
    public const int PartitionNameMaxBytes = 32;

    public const uint CommandInstallBegin = 5;

    public const uint CommandInstallStatus = 6;

    public const uint CommandInstallEnd = 7;

    public const uint CommandRemoveIncomplete = 8;

    /// <summary>Reads sectors of a game partition back, for finding out what the disk holds.</summary>
    public const uint CommandReadSectors = 9;

    public const int ReadSectorsMax = 128;

    /// <summary>Takes the CRC-32C of sectors of a game partition, read back in chunks of a given size.</summary>
    public const uint CommandChecksumSectors = 10;

    /// <summary>Deletes a whole game: its main partition and sub-partitions.</summary>
    public const uint CommandDeleteGame = 11;

    /// <summary>Rewrites a game's title, compatibility modes, transfer mode, and disc type.</summary>
    public const uint CommandUpdateGame = 12;

    public const int UpdateGameRequestBytes = EntryNameBytes + TitleBytes + 4;

    public const uint CommandFileList = 13;

    public const uint CommandFileRead = 14;

    public const uint CommandFileWrite = 15;

    public const uint CommandFileCommit = 16;

    public const uint CommandFileDelete = 17;

    // ELF_BEGIN, ELF_DATA, and ELF_RUN keep their form in every protocol version from 5 on, and
    // Loadout Bay takes them after any HELLO, so a newer Loadout can always send a newer Loadout Bay.

    /// <summary>Makes room for a new Loadout Bay ELF of the given size.</summary>
    public const uint CommandElfBegin = 18;

    /// <summary>Carries the next chunk of the ELF, at its offset.</summary>
    public const uint CommandElfData = 19;

    /// <summary>Starts the ELF from memory with a run tag; the connection then closes.</summary>
    public const uint CommandElfRun = 20;

    /// <summary>Writes the ELF over the file the running copy was started from, keeping a .bak.</summary>
    public const uint CommandElfSave = 21;

    /// <summary>Counts the block runs a file's PFS inode lists; OPL refuses a VMC with 11 or more.</summary>
    public const uint CommandFileRuns = 22;

    /// <summary>OPL takes a VMC on the hard disk only when its inode lists fewer block runs than this.</summary>
    public const int VmcMaxRuns = 11;

    public const int RunTagBytes = 16;

    public const int ElfMaxBytes = 8 * 1024 * 1024;

    /// <summary>The longest file name in OPL's folders Loadout Bay accepts.</summary>
    public const int FileNameMaxBytes = 64;

    public const int FileNameBytes = 68;

    /// <summary>A file request's folder and name, before its own fields.</summary>
    public const int FileFieldsBytes = 4 + FileNameBytes;

    public const int FileEntryBytes = FileNameBytes + 4;

    /// <summary>The most bytes one FILE_READ or FILE_WRITE carries.</summary>
    public const int FileChunkMaxBytes = 64 * 1024;

    /// <summary>
    /// What Loadout asks for per FILE_READ. Replies above about 24 KiB slow down in steps in PCSX2
    /// (0.7 s at 32 KiB, 15 s near 64 KiB), the way lost frames and TCP's retries would; 16 KiB do not.
    /// </summary>
    public const int FileReadChunkBytes = 16 * 1024;

    /// <summary>The most files one FILE_LIST reply carries.</summary>
    public const int FileListPage = 512;

    /// <summary>The last byte of an entry's name field holds the entry's flags.</summary>
    public const int EntryFlagsOffset = 39;

    /// <summary>The entry's header could not be read, so its zeros say nothing about the game.</summary>
    public const byte EntryFlagUnreadable = 0x1;

    public const int InstallBeginRequestBytes = 252;

    /// <summary>INSTALL_BEGIN flag: read the game back and compare it before the header is written.</summary>
    public const uint InstallFlagCheck = 0x1;

    /// <summary>The check takes one CRC-32C per MiB of the game.</summary>
    public const int CheckBlockBytes = 1024 * 1024;

    public const int InstallBeginReplyBytes = EntryNameBytes + 4;

    public const int InstallStatusReplyBytes = 24;

    public const int DiscIdBytes = 16;

    public const int StartupBytes = 60;

    public const int TitleBytes = 160;

    // PS2 newlib errno values, from sys/errno.h in the ps2dev toolchain.
    public const int StatusNotPermitted = -1;
    public const int StatusNotFound = -2;
    public const int StatusIo = -5;
    public const int StatusNotExecutable = -8;
    public const int StatusNoMemory = -12;
    public const int StatusBusy = -16;
    public const int StatusExists = -17;
    public const int StatusNoDevice = -19;
    public const int StatusInvalid = -22;
    public const int StatusTooLarge = -27;
    public const int StatusNoSpace = -28;
    public const int StatusBrokenPipe = -32;
    public const int StatusCopyDiffers = -138;
    public const int StatusProtocol = -71;
    public const int StatusNotImplemented = -88;
    public const int StatusProtocolMismatch = -123;

    /// <summary>Writes a request header for <paramref name="command"/> with a payload of <paramref name="payloadBytes"/>.</summary>
    public static void WriteHeader(Span<byte> header, uint command, int payloadBytes)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], command);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)payloadBytes);
    }

    /// <summary>Reads a reply header: the status and the payload length that follows.</summary>
    public static (int Status, int PayloadBytes) ReadReplyHeader(ReadOnlySpan<byte> header)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic)
        {
            throw new InvalidDataException("The reply did not come from Loadout Bay.");
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        if (length > int.MaxValue)
        {
            throw new InvalidDataException("Loadout Bay announced a reply too large to read.");
        }

        return (BinaryPrimitives.ReadInt32LittleEndian(header[4..]), (int)length);
    }

    public static BayHello ReadHello(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != HelloBaseBytes && payload.Length < HelloReplyBytes)
        {
            throw new InvalidDataException("Loadout Bay's greeting has the wrong size.");
        }

        return new BayHello(
            BinaryPrimitives.ReadUInt32LittleEndian(payload),
            Text(payload.Slice(8, VersionBytes)),
            (BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]) & HelloFlagDisk) != 0,
            payload.Length >= HelloReplyBytes ? Text(payload.Slice(HelloBaseBytes, RunTagBytes)) : string.Empty);

        static string Text(ReadOnlySpan<byte> field)
        {
            var end = field.IndexOf((byte)0);
            return Encoding.UTF8.GetString(end >= 0 ? field[..end] : field);
        }
    }

    public static BayFreeSpace ReadFreeSpace(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != FreeSpaceReplyBytes)
        {
            throw new InvalidDataException("Loadout Bay's free space reply has the wrong size.");
        }

        return new BayFreeSpace(
            BinaryPrimitives.ReadUInt64LittleEndian(payload),
            BinaryPrimitives.ReadUInt64LittleEndian(payload[8..]));
    }

    /// <summary>Reads one game entry: the partition name, then the raw HDLoader header.</summary>
    public static BayGame ReadGame(ReadOnlySpan<byte> entry)
    {
        if (entry.Length != EntryBytes)
        {
            throw new InvalidDataException("A game entry from Loadout Bay has the wrong size.");
        }

        var name = entry[..EntryNameBytes];
        var end = name.IndexOf((byte)0);
        var header = entry[EntryNameBytes..];
        var partition = Encoding.UTF8.GetString(end >= 0 ? name[..end] : name);
        // A header area of zeros, read without error, is where a copy that stopped early left off.
        var blank = (entry[EntryFlagsOffset] & EntryFlagUnreadable) == 0 && !header.ContainsAnyExcept((byte)0);
        return new BayGame(
            partition,
            HdlGameHeader.TryRead(header),
            blank && BayGame.DiscIdOfName(partition) is not null);
    }

    /// <summary>Reads a LIST_GAMES page: the total number of games and the entries in this page.</summary>
    public static (int Total, IReadOnlyList<BayGame> Games) ReadGamePage(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < ListReplyHeaderBytes)
        {
            throw new InvalidDataException("Loadout Bay's game list reply is too short.");
        }

        var total = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        if (count > ListPageGames || total > int.MaxValue
            || payload.Length != ListReplyHeaderBytes + ((int)count * EntryBytes))
        {
            throw new InvalidDataException("Loadout Bay's game list reply has the wrong size.");
        }

        var games = new BayGame[count];
        for (var i = 0; i < games.Length; i++)
        {
            games[i] = ReadGame(payload.Slice(ListReplyHeaderBytes + (i * EntryBytes), EntryBytes));
        }

        return ((int)total, games);
    }

    /// <summary>The INSTALL_BEGIN payload for <paramref name="game"/>.</summary>
    public static byte[] WriteInstallBegin(BayInstallRequest game)
    {
        ArgumentNullException.ThrowIfNull(game);
        var payload = new byte[InstallBeginRequestBytes];
        WriteText(payload.AsSpan(0, DiscIdBytes - 1), game.DiscId);
        WriteText(payload.AsSpan(16, StartupBytes - 1), game.StartupFileName);
        WriteText(payload.AsSpan(76, TitleBytes), game.Title);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(236), game.Layer0Sectors);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(240), game.Layer1Sectors);
        payload[244] = game.DiscType;
        payload[245] = game.CompatibilityModeFlags;
        payload[246] = game.TransferType;
        payload[247] = game.TransferMode;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(248), game.CheckCopy ? InstallFlagCheck : 0);
        return payload;
    }

    /// <summary>
    /// The UPDATE_GAME payload: the partition name, the title (at most 159 bytes, so it ends in a
    /// NUL), then the compatibility flags, the transfer type and mode, and the disc type.
    /// </summary>
    public static byte[] WriteUpdateGame(string partitionName, BayGameEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var payload = new byte[UpdateGameRequestBytes];
        WriteText(payload.AsSpan(0, EntryNameBytes - 1), partitionName);
        WriteText(payload.AsSpan(EntryNameBytes, TitleBytes - 1), edit.Title);
        var fields = payload.AsSpan(EntryNameBytes + TitleBytes);
        fields[0] = edit.CompatibilityModeFlags;
        fields[1] = edit.TransferType;
        fields[2] = edit.TransferMode;
        fields[3] = edit.DiscType;
        return payload;
    }

    public static BayInstallStart ReadInstallBegin(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != InstallBeginReplyBytes)
        {
            throw new InvalidDataException("Loadout Bay's install reply has the wrong size.");
        }

        var name = payload[..EntryNameBytes];
        var end = name.IndexOf((byte)0);
        return new BayInstallStart(
            Encoding.UTF8.GetString(end >= 0 ? name[..end] : name),
            (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(payload[EntryNameBytes..]), int.MaxValue));
    }

    public static BayInstallStatus ReadInstallStatus(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != InstallStatusReplyBytes)
        {
            throw new InvalidDataException("Loadout Bay's install status has the wrong size.");
        }

        return new BayInstallStatus(
            BinaryPrimitives.ReadUInt64LittleEndian(payload),
            BinaryPrimitives.ReadUInt64LittleEndian(payload[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(payload[16..]));
    }

    // UTF-8, cut at a character boundary so the field never ends inside a character.
    private static void WriteText(Span<byte> field, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var length = Math.Min(bytes.Length, field.Length);
        while (length < bytes.Length && length > 0 && (bytes[length] & 0xC0) == 0x80)
        {
            length--;
        }

        bytes.AsSpan(0, length).CopyTo(field);
    }

    /// <summary>
    /// The fields every file request starts with: the folder, and the name, NUL-padded. A name
    /// Loadout Bay would refuse is refused here.
    /// </summary>
    public static byte[] WriteFileRequest(BayFolder folder, string name, int extraBytes)
    {
        if (!IsFileName(name))
        {
            throw new ArgumentException("A file name has 1 to 64 letters, digits, '_', '.' or '-'.", nameof(name));
        }

        var request = new byte[FileFieldsBytes + extraBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(request, (uint)folder);
        Encoding.ASCII.GetBytes(name).CopyTo(request, 4);
        return request;
    }

    /// <summary>True for a name Loadout Bay accepts in OPL's folders.</summary>
    public static bool IsFileName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= FileNameMaxBytes && name is not ("." or "..")
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');

    /// <summary>Reads a FILE_LIST page: the total number of files and the files in this page.</summary>
    public static (int Total, IReadOnlyList<BayFile> Files) ReadFilePage(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8)
        {
            throw new InvalidDataException("Loadout Bay's file list reply is too short.");
        }

        var total = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        if (count > FileListPage || total > int.MaxValue || payload.Length != 8 + ((int)count * FileEntryBytes))
        {
            throw new InvalidDataException("Loadout Bay's file list reply has the wrong size.");
        }

        var files = new BayFile[count];
        for (var i = 0; i < files.Length; i++)
        {
            var entry = payload.Slice(8 + (i * FileEntryBytes), FileEntryBytes);
            var name = entry[..FileNameBytes];
            var end = name.IndexOf((byte)0);
            files[i] = new BayFile(
                Encoding.ASCII.GetString(end >= 0 ? name[..end] : name),
                BinaryPrimitives.ReadUInt32LittleEndian(entry[FileNameBytes..]));
        }

        return ((int)total, files);
    }

    /// <summary>What a negative status means, in a sentence for the user.</summary>
    public static string DescribeStatus(int status) => status switch
    {
        StatusNotPermitted => "That partition holds a game header, or is not named like a game, so Loadout Bay left it alone.",
        StatusNotFound => "Loadout Bay found no game partition with that name.",
        StatusIo => "The PS2's hard disk driver reported an error.",
        StatusBusy => "Loadout Bay is already installing a game.",
        StatusExists => "A game with this disc ID, or a partition with this name, is already on the PS2's hard disk.",
        StatusNoDevice => "Loadout Bay found no usable hard disk.",
        StatusInvalid => "Loadout Bay did not understand the request.",
        StatusTooLarge => "The game needs more partitions than the PS2's hard disk driver allows.",
        StatusNoSpace => "The game does not fit in the free space on the PS2's hard disk.",
        StatusBrokenPipe => "The game's data stopped before the last sector arrived, so it was not installed.",
        StatusCopyDiffers => "The check found that the copy on the PS2 differs from the image, so the game was not installed.",
        StatusProtocol => "Loadout Bay expected a greeting first.",
        StatusNotImplemented => "This Loadout Bay does not know that command. Update Loadout Bay.",
        StatusProtocolMismatch => "Loadout and Loadout Bay speak different protocol versions.",
        _ => "Loadout Bay failed with error " + status.ToString(CultureInfo.InvariantCulture) + ".",
    };
}

/// <summary>
/// A game to install: what INSTALL_BEGIN tells Loadout Bay. Sectors are 2048 bytes; layer 1 is
/// empty except on a DVD9. The transfer defaults are HDLGameInstaller's (UDMA mode 4).
/// </summary>
public sealed record BayInstallRequest(
    string DiscId,
    string StartupFileName,
    string Title,
    uint Layer0Sectors,
    uint Layer1Sectors,
    byte DiscType,
    byte CompatibilityModeFlags = 0,
    byte TransferType = 0x40,
    byte TransferMode = 4,
    bool CheckCopy = false)
{
    public long TotalBytes => ((long)Layer0Sectors + Layer1Sectors) * 2048;
}

/// <summary>The folders in OPL's partition that Loadout Bay reads and writes.</summary>
public enum BayFolder : uint
{
    /// <summary>Artwork: <c>ART/&lt;startup&gt;_&lt;kind&gt;.png</c>.</summary>
    Art = 0,

    /// <summary>Per-game settings: <c>CFG/&lt;startup&gt;.cfg</c>.</summary>
    Cfg = 1,

    /// <summary>
    /// Virtual memory cards: <c>VMC/&lt;name&gt;.bin</c>. Loadout Bay never replaces a card on a
    /// commit; replacing one takes a delete first.
    /// </summary>
    Vmc = 2,

    /// <summary>
    /// Loadout Bay as an OPL app: <c>APPS/Loadout-Bay</c>, with the ELF and the <c>title.cfg</c> that
    /// lists it in OPL's Apps menu.
    /// </summary>
    App = 3,
}

/// <summary>A file in one of OPL's folders, with its size in bytes.</summary>
public sealed record BayFile(string Name, uint Size);

/// <summary>
/// What UPDATE_GAME writes into a game's header. Loadout Bay keeps every other byte, including the
/// HDL compatibility flags.
/// </summary>
public sealed record BayGameEdit(string Title, byte CompatibilityModeFlags, byte TransferType, byte TransferMode, byte DiscType)
{
    /// <summary>The header's current values, to change only what the user changed.</summary>
    public static BayGameEdit Of(HdlGameHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        return new BayGameEdit(header.Title, header.CompatibilityModeFlags, header.TransferType, header.TransferMode, (byte)header.DiscType);
    }
}

/// <summary>Loadout Bay's answer to INSTALL_BEGIN: the new partition and how many parts it has.</summary>
public sealed record BayInstallStart(string PartitionName, int Parts);

/// <summary>
/// How far an install is: the game's size, the bytes Loadout Bay received, and the bytes on its
/// disk. <see cref="Checking"/> is set once every byte is sent and Loadout Bay is reading the game back.
/// </summary>
public sealed record BayInstallStatus(ulong TotalBytes, ulong ReceivedBytes, ulong WrittenBytes, bool Checking = false);

/// <summary>Loadout Bay's answer to HELLO.</summary>
/// <param name="RunTag">The tag Loadout started this copy with over ELF_RUN, or empty when a launcher started it.</param>
public sealed record BayHello(uint ProtocolVersion, string Version, bool HasHardDisk, string RunTag = "");

/// <summary>Hard disk size and free space, in 512-byte sectors as the PS2's driver counts them.</summary>
public sealed record BayFreeSpace(ulong TotalSectors, ulong FreeSectors)
{
    public ulong TotalBytes => TotalSectors * FreeSpace.ApaSectorSizeBytes;

    public ulong FreeBytes => FreeSectors * FreeSpace.ApaSectorSizeBytes;
}
