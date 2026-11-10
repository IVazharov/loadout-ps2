# Speed test

The speed test measures how fast game data can travel from the PC to the PS2. Loadout Bay receives the data into memory and throws it away, or, in a disk run, writes it to the hard disk. It times each run itself. `tools/Loadout.SpeedTest` sends the data and prints the PC's figure next to Loadout Bay's.

1 MB is 1048576 bytes. Every number on the wire is little-endian.

## Run it

1. Start Loadout Bay on the PS2. The speed test listens on TCP 45070 and UDP 45071; the network settings screen (Triangle) lists the ports.
2. On the PC, from the repository root:

```powershell
dotnet run --project tools/Loadout.SpeedTest -c Release -- 192.168.1.50 tcp --mb 256
dotnet run --project tools/Loadout.SpeedTest -c Release -- 192.168.1.50 udp --mb 256 --rate 6,8,10,12
```

A disk run writes the TCP data to the hard disk:

```powershell
dotnet run --project tools/Loadout.SpeedTest -c Release -- 192.168.1.50 tcp --mb 256 --disk
```

`--rate` paces UDP in MB/s, and a list runs one test per rate. `--rate 0` sends as fast as the PC can, which mostly measures how many packets the PS2 drops. `--datagram` sets the UDP packet size, up to 1472 bytes.

Loadout Bay shows each run on its screen, and the tool prints both measurements.

## Disk runs

A disk run removes any partition named `LOADOUT.SPEEDTEST`, creates a new one just big enough for the data, and writes the data after the partition's 4 MB header area, the way game data is stored. Each 256 KiB block is copied to IOP memory and written with the hard disk driver's `HIOCTRANSFER` command. Writes go through a 16 MB ring of 64 blocks: a writer thread, one priority step below the receiver, writes the oldest block while new ones are received, and a slow write does not stall the network. The time runs until the last block is on the disk. The partition is removed afterwards. With `--keep` it stays, so the data can be checked; the next disk run replaces it.

The partition is typed PFS but never formatted, and its name has no `PP.` prefix, so Open PS2 Loader does not list it as a game. A disk run writes nothing else on the disk. The data size must be a whole number of 512-byte sectors.

`--unit` sets how many KiB each disk write covers, and a list such as `--unit 32,64,128,256` runs one disk test per size. On the console, smaller writes were slower: each write has a fixed cost. Writes of 512 KiB, gathered from two blocks, were no faster than 256 KiB, and a 1 MiB buffer did not fit in IOP memory.

## TCP, port 45070

The PC connects and sends a 16-byte header, then the data.

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | magic `LBT1` (0x3154424C) |
| 4 | 4 | flags: bit 0 writes to the hard disk, bit 1 keeps the test partition, bits 16-31 set the size of each disk write in KiB (16, 32, 64, 128, or 256; 0 means 256) |
| 8 | 8 | total data bytes that follow |

After the last byte, Loadout Bay replies with 24 bytes and closes the connection:

| Offset | Size | Field |
|---|---|---|
| 0 | 8 | data bytes received |
| 8 | 8 | microseconds from the first data byte to the last, writes included |
| 16 | 4 | status: 0, or a negative errno when a disk run failed |
| 20 | 4 | reserved, 0 |

## UDP, port 45071

Each datagram starts with a 12-byte header, followed by any payload.

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | magic `LBU1` (0x3155424C) |
| 4 | 4 | run ID, chosen by the PC |
| 8 | 4 | sequence number, from 0 |

A new run ID starts a new count. The end packet has sequence number 0xFFFFFFFF and 4 more bytes: the number of data packets sent. The PC repeats it until Loadout Bay replies, because it can be lost like any other packet. Loadout Bay answers every end packet for the current run with 32 bytes:

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | magic `LBR1` (0x3152424C) |
| 4 | 4 | run ID |
| 8 | 4 | data packets received |
| 12 | 4 | data packets sent, copied from the end packet |
| 16 | 8 | bytes received, headers included |
| 24 | 8 | microseconds from the first data packet to the last |

A run with no packet for 3 seconds ends without a reply.

The tool has other commands for working with Loadout Bay. Run it without arguments for the list.

## Original discs in a PC drive

A proof of concept for installing a game from its original disc. The disc commands open the drive (such as `E:`) without admin rights and read its 2048-byte sectors, the same bytes as the Redump iso.

- `disc-info E:` shows the disc type, size and layers, and the game's disc ID.
- `read-image E: [--out <iso>]` reads the whole disc, prints the speed and the MD5 and SHA-1 that Redump lists, and can save it as an iso.
- `<ps2-address> install E: [--check]` streams the disc straight to Loadout Bay.
- `disc-copy E: <iso> [--minutes <n>] [--retry-all] [--sweep <n>]` copies a damaged disc to an iso. A fast pass reads it in order and jumps past weak spots. Then sweeps go over what is missing in pieces of 128, 16 or 1 sectors: smaller after a failure, bigger after good reads, every second sweep from the end backwards, and the rest of a dead 16-sector error-correction block is left for the next sweep. After each failed read the drive is rested until it reads again, because some drives refuse every read for half a minute after one fails. Progress is kept in `<iso>.map`, so the same command goes on after a stop, after cleaning the disc, or in another drive. `--retry-all` starts the sweeps over, as after cleaning.
- `disc-map`, `disc-probe` and `disc-fastfail` are the experiments that shaped `disc-copy`.

Tested with one slim USB drive: a clean DVD read at 5.4 MB/s and matched Redump; a badly scratched one took a 20-minute fast pass to 97%, then sweeps and two cleanings to a full copy that matched Redump.
