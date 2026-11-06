# Loadout Bay control protocol

Loadout talks to Loadout Bay over this protocol to read the PS2's game library, to install, delete, and edit games on the PS2's hard disk, and to read and write Open PS2 Loader's artwork and game settings. A game's data travels over a second connection, the data connection, which `INSTALL_BEGIN` opens the way for. The speed test has its own ports and format, described in [speed-test.md](speed-test.md).

## Connection

Loadout Bay listens on TCP port 45060 for the control connection and on TCP port 45061 for the data connection. The PC always opens both, so the PC needs no inbound firewall rule. Loadout Bay serves one control connection at a time. While one is open, a second connection and the speed test wait. Loadout Bay closes a control connection that sends no request for 30 seconds, unless game data is arriving on its data connection.

Every number is little-endian. Strings are UTF-8, padded with NUL bytes to their field size.

## Discovery

Settings' Find Bay locates Loadout Bay without its address. Loadout sends an 8-byte probe from each of the PC's IPv4 addresses to UDP port 45060, at that subnet's broadcast address and at 255.255.255.255 (a Loadout Bay whose netmask differs from the PC's, as PCSX2's DHCP hands out, hears only the second). It sends the probe three times in 1.5 seconds and takes every answer in that time.

| Offset | Size | Probe field |
|---|---|---|
| 0 | 4 | Magic `0x3144424C` ("LBD1") |
| 4 | 4 | The PC's protocol version |

Loadout Bay answers the sender alone with 28 bytes, and ignores any other datagram on the port. The PC takes Loadout Bay's address from the answer's source.

| Offset | Size | Answer field |
|---|---|---|
| 0 | 4 | Magic `0x3144424C` |
| 4 | 4 | Loadout Bay's protocol version |
| 8 | 4 | Flags: bit 0 set when a hard disk is ready |
| 12 | 16 | Loadout Bay's version, such as `0.9.0` |

Loadout Bay answers from its main loop, so a probe that arrives during a control connection waits until it ends. When no Loadout Bay answers, Loadout greets every address of the PC's subnets with HELLO on the control port, 1.5 seconds each and 512 at a time, which also finds a Loadout Bay when broadcasts do not get through. A subnet larger than a /22 is swept only in the /24 around the PC's address. The PC needs no inbound firewall rule: Windows lets the unicast answer to a broadcast through for a few seconds.

## Requests and replies

The PC sends a 12-byte header and then the payload:

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | magic `LBC1` (0x3143424C) |
| 4 | 4 | command |
| 8 | 4 | payload length, at most 65664 (64 KiB + 128) |

Loadout Bay answers each request with a 12-byte header and then the payload:

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | magic `LBC1` (0x3143424C) |
| 4 | 4 | status: 0, or a negative errno value |
| 8 | 4 | payload length |

A request with the wrong magic or a payload over 65664 bytes gets no reply: Loadout Bay closes the connection. An unknown command gets status `-ENOSYS` and no payload. Every command other than `HELLO` gets `-EPROTO` until a `HELLO` with a matching protocol version has succeeded on that connection, apart from `ELF_BEGIN`, `ELF_DATA`, and `ELF_RUN`, which follow any `HELLO` (see [A new Loadout Bay](#a-new-loadout-bay)).

## Commands

### 1: HELLO

Request payload, 4 bytes: the PC's protocol version (currently 8).

Reply payload, 40 bytes:

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | Loadout Bay's protocol version |
| 4 | 4 | flags: bit 0 is set when a usable hard disk was found |
| 8 | 16 | Loadout Bay's version, major.minor.patch, such as `0.9.0` |
| 24 | 16 | the run tag this copy was started with by `ELF_RUN`, NUL-padded; all zeros when a launcher started it |

When the protocol versions differ, the status is `-EPROTONOSUPPORT` and the payload is still sent, so the PC can name the Loadout Bay version it found.

### 2: FREE_SPACE

No request payload.

Reply payload, 16 bytes:

| Offset | Size | Field |
|---|---|---|
| 0 | 8 | total sectors on the hard disk, 512 bytes each (`HDIOC_TOTALSECTOR`) |
| 8 | 8 | free sectors the hard disk driver can still hand out as new partitions (`HDIOC_FREESECTOR`). APA partition sizes are powers of two, so this can be less than the unused space. |

Without a usable hard disk the status is `-ENODEV` and there is no payload.

### 3: LIST_GAMES

Request payload, 4 bytes: the number of the first game to send, starting at 0.

Reply payload: 8 bytes, then up to 64 game entries:

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | total number of games on the disk |
| 4 | 4 | number of entries in this reply |
| 8 | 1064 each | game entries |

A game is an APA partition of type `0x1337` (HDLoader) that is not a sub-partition, in the disk's partition order. The PC asks again from the next number until it has them all. A total that changes between replies means the disk changed while it was read.

Without a usable hard disk the status is `-ENODEV` and there is no payload.

### 4: READ_ENTRY

Request payload: the partition name, 1 to 32 bytes, without `hdd0:` and without a trailing NUL. A comma is refused, because it would start a partition password.

Reply payload: one game entry. The status is `-ENOENT` when no game partition has that name.

### 5: INSTALL_BEGIN

Request payload, 252 bytes:

| Offset | Size | Field |
|---|---|---|
| 0 | 16 | disc ID, such as `SLUS-20043`: 1 to 10 letters, digits, `-` or `_`, NUL-padded |
| 16 | 60 | startup file name, such as `SLUS_200.43`, NUL-padded, at most 59 bytes |
| 76 | 160 | title, UTF-8, NUL-padded |
| 236 | 4 | layer 0 sectors (2048 bytes each) |
| 240 | 4 | layer 1 sectors, 0 except on a DVD9: the sectors after layer 0. Layer 1's own volume starts 16 sectors before layer 0 ends, so this is its volume size − 16. |
| 244 | 1 | disc type, as the header stores it: `0x12` CD, `0x14` DVD |
| 245 | 1 | compatibility flags (`ops2l_compat_flags`) |
| 246 | 1 | transfer type, `0x40` for UDMA |
| 247 | 1 | transfer mode, 4 by default |
| 248 | 4 | flags: bit 0 asks for the check (see `INSTALL_END`) |

Loadout Bay names the game the way HDLGameInstaller does: `PP.<disc ID>.HDL.<title>`, keeping the first 14 characters of the title and turning each one that is not a letter, a digit or `_` into `_`. A character outside ASCII becomes one `_`. Loadout Bay first removes every incomplete install of the same disc ID (see `REMOVE_INCOMPLETE`), such as one cut off when the PS2 lost power, so the new install replaces it. The status is `-EEXIST` when a partition still has that name, or when a whole game with the same disc ID is on the disk.

Loadout Bay then creates the partitions:

- APA partition sizes run from 128 MB to 4 GB, and the largest the disk allows is `HDIOC_MAXSECTOR`.
- The main partition keeps its first 4 MB for headers, and each sub-partition its first 2048 bytes.
- Each part takes the smallest size that holds what is left of the game, or the largest the disk allows. It takes one size smaller when that would leave a whole 128 MB unused.
- When the disk has no room for a size, the part tries one size smaller.

Loadout Bay clears the place where the header goes, so a header left behind by a removed game is never read as this one's. It does not write the header yet.

Reply payload, 44 bytes:

| Offset | Size | Field |
|---|---|---|
| 0 | 40 | partition name, NUL-padded |
| 40 | 4 | number of parts: the main partition and its sub-partitions |

Other statuses:

- `-EBUSY`: an install is already open on this connection.
- `-ENOSPC`: the partitions do not fit.
- `-EFBIG`: the game needs more than 64 sub-partitions.
- `-EINVAL`: the request cannot be a game.
- `-ENODEV`: there is no usable hard disk.

When creation fails part-way, the new partitions are removed again.

### The data connection

After a successful `INSTALL_BEGIN`, the PC connects to TCP port 45061 and sends the game's sectors in order: (layer 0 + layer 1) × 2048 bytes, raw, with no framing.

- Loadout Bay accepts one data connection per install. A connection that was already waiting before `INSTALL_BEGIN` is refused.
- Each 256 KiB is written as it arrives, through the speed test's 16 MB ring, filling the parts in order.
- With the check flag, the game's last byte is followed by its checksums: one CRC-32C (Castagnoli, as the PC's CRC32 instruction computes it) per MiB of the game, the last one over what is left, 4 bytes each, little-endian. The check covers games up to 16 GiB; a larger one is refused with `-EFBIG`.
- Loadout Bay closes the data connection after the last byte (or the last checksum), or when a disk write fails.

### 6: INSTALL_STATUS

No request payload.

Reply payload, 24 bytes:

| Offset | Size | Field |
|---|---|---|
| 0 | 8 | the game's size in bytes |
| 8 | 8 | bytes received on the data connection |
| 16 | 8 | bytes written to the disk |

The status is 0, or the first disk write error. The payload is sent with an error too, so the PC can say how far the copy got. Without an open install the status is `-ENOENT` and there is no payload.

### 7: INSTALL_END

No request payload. A reply payload only when the install failed, 8 bytes:

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | with `-EILSEQ`, the first MiB of the game that differs; otherwise 0 |
| 4 | 4 | 1 when the game's partitions were removed, 0 when removing them failed |

The PC sends `INSTALL_END` after its last byte, or after it stops sending. Loadout Bay then:

1. receives what is still on its way;
2. waits until everything received is on the disk;
3. with the check flag, reads the whole game back from the disk, part by part as it was written, and compares the CRC-32C of each MiB with the one the PC sent, stopping at the first that differs;
4. only when every byte of the game is on the disk, and the check found no difference, writes the game's header and closes the install.

The check exists because data can be spoiled between Loadout Bay's memory and the disk, after the network's own checks have passed. PCSX2's emulated network adapter and hard disk do this now and then: a received network frame lands in the data being written. The check reads the game back at the disk's speed, so `INSTALL_END` can take minutes; the PC waits up to one second per MiB, plus 30 seconds.

The status is 0 when the game is installed. Otherwise no header is written, and Loadout Bay removes the game's partitions, as a game without its header is of no use. The status is then `-EPIPE` when the data or its checksums stopped before the end, `-EILSEQ` when the check found a difference, or else the first disk error. Without an open install the status is `-ENOENT`, and the payload's second field is 0.

When the control connection closes with an install open, as when Loadout is closed or the PC loses the network, the install ends the same way: without a header, and its partitions removed.

Only a PS2 that loses power, or a removal that fails, leaves an incomplete install on the disk, which `LIST_GAMES` lists with a zero header. The next `INSTALL_BEGIN` of that disc replaces it, and `REMOVE_INCOMPLETE` removes it.

The header comes last on purpose; this is the one difference from HDLGameInstaller, which writes the header before the data. With the header last, a copy that stops early can never be listed as a whole game.

### 8: REMOVE_INCOMPLETE

Request payload: the partition name, 1 to 32 bytes, without `hdd0:` and without a trailing NUL. No reply payload.

Removes an incomplete install: the main partition and its sub-partitions. Loadout Bay reads the partition's header again, so a list read earlier cannot remove a game that has been installed since. It removes the partition only when all of these hold:

- it is a game partition (type `0x1337`, not a sub-partition);
- its name has the form `PP.<disc ID>.HDL.<title>`, with a disc ID as `INSTALL_BEGIN` accepts it;
- its header was read without error, and all 1024 bytes of it are zeros, the way an install that stopped early leaves it.

Statuses:

- `-ENOENT`: no game partition has that name.
- `-EPERM`: the name is not in that form, or the partition holds a header. Nothing is removed.
- `-EBUSY`: the game is being installed right now.
- `-EIO`: the header could not be read. Nothing is removed.

### 9: READ_SECTORS

Reads part of a game partition back, to find out what the disk holds. Loadout does not use it; the speed test tool's `read-sectors` command does.

Request payload, 52 bytes:

| Offset | Size | Field |
|---|---|---|
| 0 | 40 | partition name, NUL-padded, without `hdd0:` |
| 40 | 4 | part: 0 for the main partition, then 1 for the first sub-partition, and so on |
| 44 | 4 | first sector (512 bytes) in that part, counted from the part's start, as the hard disk driver's `HIOCTRANSFER` counts it |
| 48 | 4 | number of sectors, 1 to 128 |

Reply payload: the sectors, 512 bytes each. They are read through the same path the check reads a game back.

The hard disk driver does not read a part's reserved area: sectors below 0x2000 in the main partition, or below 2 in a sub-partition, are refused with `-EINVAL`. So the game's header cannot be read this way; `READ_ENTRY` returns it.

### 10: CHECKSUM_SECTORS

Takes the CRC-32C of part of a game partition, so a large range can be compared with the PC's copy without sending it. The speed test tool's `checksum-sectors` command uses it.

Request payload, 56 bytes: the 52 bytes of `READ_SECTORS`, with no limit on the number of sectors, then:

| Offset | Size | Field |
|---|---|---|
| 52 | 4 | sectors per read, 1 to 512 |

Reply payload, 4 bytes: the CRC-32C of the sectors, computed the way the check computes it.

For both commands, a name is found the way `READ_ENTRY` finds it, and a header that cannot be read does not stop the read. Statuses:

- `-ENOENT`: no game partition has that name.
- `-EBUSY`: an install is open.
- `-EINVAL`: the request has the wrong size, a count is out of range, or the sectors start in the reserved area.
- `-ENODEV`: there is no usable hard disk.
- Any other negative status is the hard disk driver's error.

### 11: DELETE_GAME

Request payload: the partition name, 1 to 32 bytes, without `hdd0:` and without a trailing NUL. No reply payload.

Deletes a whole game: the main partition and its sub-partitions. Loadout Bay reads the partition's header again and deletes it only when all of these hold:

- it is a game partition (type `0x1337`, not a sub-partition);
- its name has the form `PP.<disc ID>.HDL.<title>`, with a disc ID as `INSTALL_BEGIN` accepts it;
- its header has the HDLoader magic `0xDEADFEED`.

The game's files in `+OPL`, such as its artwork and its CFG file, stay. An incomplete install goes through `REMOVE_INCOMPLETE` instead.

Statuses:

- `-ENOENT`: no game partition has that name.
- `-EPERM`: the name is not in that form, or the header is not a game's. Nothing is removed.
- `-EBUSY`: an install is open.
- `-EIO` or another driver error: the header could not be read, or the removal failed.

### 12: UPDATE_GAME

Rewrites the fields of a game's header that Loadout lets the user edit.

Request payload, 204 bytes:

| Offset | Size | Field |
|---|---|---|
| 0 | 40 | partition name, NUL-padded, without `hdd0:` |
| 40 | 160 | title, UTF-8, 1 to 159 bytes, NUL-padded |
| 200 | 1 | compatibility flags (`ops2l_compat_flags`) |
| 201 | 1 | transfer type |
| 202 | 1 | transfer mode |
| 203 | 1 | disc type: `0x12` CD, `0x13` CDDA, `0x14` DVD, or the one the header already has |

Reply payload: the game's entry, read back from the disk after the write.

Current Open PS2 Loader takes a game's title and disc type from this header, but its compatibility modes and transfer mode from the game's CFG file in `+OPL` (`$Compatibility` and `$DMA`). Only HDLGameInstaller and older loaders read those two header fields, so Loadout sends them back as the header holds them.

Loadout Bay accepts a partition the way `DELETE_GAME` does. It reads the header, changes those fields, and writes the 1024 bytes back in one write. Every other byte stays, including the HDL compatibility flags at offset 168, which HDLGameInstaller's update sets to 0. The partition name keeps the old title, as with HDLGameInstaller.

Statuses:

- `-ENOENT`, `-EPERM`, `-EBUSY`: as for `DELETE_GAME`. Nothing is written.
- `-EINVAL`: the request has the wrong size, the name does not end in a NUL, the title is empty or fills its field, or the disc type is not allowed.
- `-EIO`: the header read back after the write differs from the one written.

### OPL's files

Commands 13 to 17 and 22 read and write files in four folders of the partition Open PS2 Loader keeps its own files in, and 23 measures its free space: `ART` (artwork, `<startup>_COV.png` and so on), `CFG` (game settings, `<startup>.cfg`), `VMC` (virtual memory cards, `<name>.bin`), and `APPS/Loadout-Bay` (Loadout Bay itself as an OPL app: `loadout-bay.elf`, the `title.cfg` that lists it in OPL's Apps menu, and an optional `loadout-bay.cfg`). Loadout Bay finds that partition the way OPL does: `hdd0:__common` holds `OPL/conf_hdd.cfg`, whose `hdd_partition` names it, and without that file it is `+OPL`. A partition whose name starts with `+` holds the folders at its root; any other holds them in an `OPL` folder. Loadout Bay never creates the partition, which OPL makes the first time it lists games from the hard disk. It creates a folder when a file is first written there.

The partition is mounted for the first file command and unmounted when the connection ends. While an install is open, every file command gets `-EBUSY`.

Every file request but `FILE_LIST` starts with these 72 bytes:

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | folder: 0 `ART`, 1 `CFG`, 2 `VMC`, 3 `APPS/Loadout-Bay` |
| 4 | 68 | file name, NUL-padded: 1 to 64 letters, digits, `_`, `.` or `-`, not `.` or `..`; in `VMC`, ending in `.bin` |

A memory card may hold saves, so Loadout Bay never replaces one: a `FILE_WRITE` at offset 0 or a `FILE_COMMIT` for a card that is already in `VMC` gets `-EEXIST`, and the commit deletes its `.part` file. Replacing a card takes a `FILE_DELETE` first.

Common statuses:

- `-EINVAL`: an unknown folder, or the request has the wrong size.
- `-EPERM`: the file name breaks the rules above.
- `-EEXIST`: the memory card is already there.
- `-ENOENT`: no OPL partition, or no such file.
- `-EBUSY`: an install is open.
- `-ENODEV`: there is no usable hard disk.

### 13: FILE_LIST

Request payload, 8 bytes: the folder, and the number of the first file to send, starting at 0.

Reply payload: the total number of files (4 bytes), the number in this reply (4 bytes), then up to 512 entries of 72 bytes: the name field as above, then the size in bytes (4 bytes). Only regular files with names that keep the rules are listed. A folder that does not exist yet has no files.

### 14: FILE_READ

Request payload, 80 bytes: the 72 bytes above, the offset to read from (4 bytes), and the number of bytes wanted (4 bytes, at most 65536).

Reply payload: the bytes read. Fewer than asked means the file ends there. Loadout asks for 16 KiB at a time: in PCSX2, larger replies slow down in steps, to 15 seconds near 64 KiB.

### 15: FILE_WRITE

Request payload: the 72 bytes above, the offset (4 bytes), then 1 to 65536 bytes of the file. No reply payload.

The bytes go into `<name>.part`, never into the file itself. The offset must be the `.part` file's size so far, so a missing chunk is noticed; offset 0 starts the `.part` file again. Another offset gets `-EINVAL`.

### 16: FILE_COMMIT

Request payload, 76 bytes: the 72 bytes above and the file's size (4 bytes). No reply payload.

When `<name>.part` holds exactly that many bytes, Loadout Bay deletes the file, if there is one, and renames `<name>.part` to it, then flushes the partition. Until the commit the old file stays whole, so a transfer that stops early leaves only a `.part` file, which the next transfer of that name starts again. Another size gets `-EPIPE`, and nothing changes.

### 17: FILE_DELETE

Request payload, 72 bytes: the fields above. No reply payload. Deletes the file and flushes the partition.

### 22: FILE_RUNS

Request payload, 72 bytes: the fields above. Reply payload, 4 bytes: the number of block runs the file's PFS inode lists, its own block included (`number_data`, which ps2sdk's PFS driver gives in the stat's `private_3`). OPL reads the same count from the inode and refuses a VMC on the hard disk as fragmented when it is 11 or more, so Loadout checks a new card with it.

### 23: FILE_SPACE

No request payload. Reply payload, 8 bytes: the free bytes in OPL's partition, the free zones times the zone size (`PDIOC_ZONEFREE` and `PDIOC_ZONESZ` of ps2sdk's PFS driver). New memory cards go there, and the partition is often far smaller than the hard disk, so Loadout checks that the cards it is about to make fit. The statuses are the file commands' common ones.

### A new Loadout Bay

Loadout sends a new Loadout Bay ELF to the running copy, which starts it from memory. The new copy then gets the same ELF again and saves it over the file it was started from. `ELF_BEGIN`, `ELF_DATA`, and `ELF_RUN` keep the form below in every protocol version from 5 on and are taken after any `HELLO`, even one whose versions differ, so a newer Loadout can always send a newer Loadout Bay. `ELF_SAVE` needs a matching `HELLO`, like the other commands.

### 18: ELF_BEGIN

Request payload, 4 bytes: the ELF's size, 1 byte to 8 MiB. No reply payload. Makes room for the ELF in memory and drops one received before. A size out of range gets `-EINVAL`; no room gets `-ENOMEM`.

### 19: ELF_DATA

Request payload: the offset (4 bytes), then 1 to 65536 bytes of the ELF. No reply payload. The offset must be the number of bytes received so far; another offset, or more bytes than the size left, gets `-EINVAL`. Without `ELF_BEGIN` the status is `-ENOENT`.

### 20: ELF_RUN

Request payload, 16 bytes: the run tag, 1 to 16 letters, digits, or dashes, NUL-padded. No reply payload.

When the whole ELF is there and ps2sdk's loader can place it (`elf_loader_is_elf_valid_for_loading`), the status is 0. After the reply, Loadout Bay closes the connection, waits a second, resets the IOP, and starts the ELF from memory with ps2sdk's `elf-loader2`. Its `argv[0]` is the running copy's own, so it reads `loadout-bay.cfg` from the same place, and its `argv[1]` is `--run-tag=<tag>`, which its `HELLO` then names. A partial ELF or a bad tag gets `-EINVAL`; a file the loader cannot place gets `-ENOEXEC`, and the running copy carries on.

### 21: ELF_SAVE

No request or reply payload. Writes the ELF received over the file in this copy's `argv[0]`, which must be on a USB drive (`mass`) or on the hard disk; otherwise the status is `-EPERM`. OPL starts an app on the hard disk with an `argv[0]` such as `hdd0:+OPL:pfs0:APPS/Loadout-Bay/loadout-bay.elf`: the partition, then the path on it. Loadout Bay mounts that partition to save there, and to read `loadout-bay.cfg` beside the ELF when it starts. The ELF goes to `<file>.part` first and is read back and compared (`-EIO` when it differs). Then the old file becomes `<file>.bak`, replacing an older `.bak`, and `<file>.part` takes the file's name. If that last rename fails, the old file gets its name back. A partial ELF gets `-EINVAL`, one the loader cannot place `-ENOEXEC`.

### Game entry

| Offset | Size | Field |
|---|---|---|
| 0 | 40 | partition name, NUL-padded; a name is at most 32 bytes |
| 39 | 1 | flags: bit 0 is set when the header could not be read |
| 40 | 1024 | the game's HDLoader header, as it is on the disk |

Loadout Bay opens the partition read-only and reads the header 1 MB into it. Reads on an open APA partition start 4 KB in, so that is 1 MB + 4 KB from the partition's first sector, where HDLGameInstaller writes it.

The PC tells three cases apart:

- **Unreadable:** the header could not be read. The entry carries zeros there with flag bit 0 set, and the PC shows the game as unreadable.
- **Incomplete install:** the header was read and is all zeros, under a name of the form `PP.<disc ID>.HDL.<title>`. The PC shows it under its disc ID and offers `REMOVE_INCOMPLETE`, which runs only after the user confirms.
- **Unrecognised:** any other header without the HDLoader magic is shown as unreadable too, and cannot be removed this way.

The PC decodes the header (`HdlGameHeader`):

| Offset | Field |
|---|---|
| 0 | magic `0xDEADFEED` |
| 6 | version, 1 |
| 8 | title, 160 bytes, UTF-8 |
| 169 | compatibility flags (`ops2l_compat_flags`) |
| 170, 171 | transfer type and mode |
| 172 | startup file name, 60 bytes, such as `SLUS_206.75`, which gives the disc ID `SLUS-20675` |
| 232 | layer 1 start: layer 0 sectors − 16 on a DVD9, otherwise 0 |
| 236 | disc type |
| 240 | number of parts |
| 244 | 12 bytes per part: its first game sector, its first data sector on the disk (512 bytes, after the part's reserved area), and its size in bytes |

The game size is the sum of each part's size divided by 2048. `INSTALL_END` writes the header in this same form.

## Status values

These are the PS2 newlib errno values, from `sys/errno.h` in the ps2dev toolchain.

| Status | Name | Meaning here |
|---|---|---|
| -1 | `EPERM` | the partition is not the kind of game the command takes, a file name breaks the rules, or `ELF_SAVE` finds that this copy was not started from a USB drive or the hard disk, so nothing was changed |
| -2 | `ENOENT` | no game partition or file with that name, no OPL partition, or no install open |
| -20 | `ENOTDIR` | `conf_hdd.cfg` names OPL's partition in a form Loadout Bay won't use: an empty name, or one with anything but letters, digits, `+`, `_`, `.` and `-` |
| -5 | `EIO` | the hard disk driver failed, a header read back differs from the one written, or a saved ELF read back differs |
| -8 | `ENOEXEC` | the ELF sent cannot be started |
| -12 | `ENOMEM` | no room in memory for the ELF sent |
| -16 | `EBUSY` | an install is open |
| -17 | `EEXIST` | the partition name or the disc ID is already on the disk, or a memory card of that name is in `VMC` |
| -19 | `ENODEV` | no usable hard disk |
| -22 | `EINVAL` | the request payload has the wrong size, or cannot be a game |
| -27 | `EFBIG` | the game needs more sub-partitions than APA allows, or is over 16 GiB with the check |
| -28 | `ENOSPC` | the game's partitions do not fit on the disk |
| -32 | `EPIPE` | the game's data stopped before its last byte, or a file's `.part` does not have the size its commit names |
| -138 | `EILSEQ` | the check found that the copy on the disk differs from what the PC sent |
| -71 | `EPROTO` | no successful `HELLO` on this connection yet |
| -88 | `ENOSYS` | unknown command |
| -123 | `EPROTONOSUPPORT` | the protocol versions differ |
