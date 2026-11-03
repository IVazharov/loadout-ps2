#!/usr/bin/env python3
"""Makes the chd files ChdFileTests reads, with MAME's chdman, from made-up data: no game is involved.

    python make-fixtures.py <path to chdman>

For each file, expected.txt keeps what chdman itself extracts from it: the SHA-256 of the iso or bin,
and for a CD the cue sheet in sheets/. Run this only to add a case: Loadout's tests do not need chdman.
"""
import hashlib, math, os, random, subprocess, sys, tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
SECTOR = 2048
RAW = 2352


def tone(bytes_count, left_hz=440.0, right_hz=660.0, little=True):
    """16-bit stereo, which FLAC compresses well."""
    out = bytearray()
    order = 'little' if little else 'big'
    for n in range(bytes_count // 4):
        out += int(9000 * math.sin(n * left_hz / 7000)).to_bytes(2, order, signed=True)
        out += int(7000 * math.sin(n * right_hz / 7000)).to_bytes(2, order, signed=True)
    return bytes(out)


def dvd_image():
    """Parts that different compressors win on: silence, tones, skewed bytes, text, and noise."""
    rng = random.Random(5150)
    parts = [
        bytes(SECTOR * 16),                                                 # repeated hunks
        tone(SECTOR * 64),
        tone(SECTOR * 32, 220.0, 225.0, little=False),
        bytes(rng.choice(b'\x00\x00\x00\x00\x01\x02\x03\xff') for _ in range(SECTOR * 32)),
        (b'BOOT2 = cdrom0:\\SLUS_999.99;1\nVER = 1.00\nVMODE = NTSC\n' * 2000)[:SECTOR * 32],
        rng.randbytes(SECTOR * 8),
        bytes(SECTOR * 16),
    ]
    return b''.join(parts)


# The error correction of a CD sector, after MAME's cdrom.cpp.
LOW = [((i << 1) ^ (0x11D if i & 0x80 else 0)) & 0xFF for i in range(256)]
HIGH = [0] * 256
for i in range(256):
    HIGH[i ^ LOW[i]] = i


def parity(sector, offsets):
    first = second = 0
    for offset in offsets:
        value = 0 if sector[15] == 2 and offset < 4 else sector[12 + offset]
        first ^= value
        second ^= value
        first = LOW[first]
    first = HIGH[LOW[first] ^ second]
    return first, second ^ first


def add_ecc(sector):
    for row in range(86):
        a, b = parity(sector, [row + 86 * c for c in range(24)])
        sector[0x81C + row], sector[0x81C + 86 + row] = a, b
    for row in range(52):
        a, b = parity(sector, [2 * (((row >> 1) * 43 + c * 44) % 1118) + (row & 1) for c in range(43)])
        sector[0x8C8 + row], sector[0x8C8 + 52 + row] = a, b


def data_sector(lba, mode, rng, good_ecc=True):
    sector = bytearray(RAW)
    sector[0:12] = bytes([0] + [0xFF] * 10 + [0])
    frames = lba + 150
    sector[12:15] = bytes(int(str(x), 16) for x in (frames // 4500, frames // 75 % 60, frames % 75))
    sector[15] = mode
    start = 16 if mode == 1 else 24
    if mode == 2:
        sector[16:24] = bytes([0, 0, 8, 0, 0, 0, 8, 0])
    sector[start:start + 2048] = (b'Loadout chd test data %d ' % lba * 200)[:1024] + rng.randbytes(16) * 64
    if good_ecc:
        add_ecc(sector)
    return bytes(sector)


def cd_data_track(frames, mode, first_lba=0):
    rng = random.Random(1999 + mode)
    # Every eighth sector's error correction is left wrong, so it is stored as it is.
    return b''.join(data_sector(first_lba + n, mode, rng, good_ecc=n % 8 != 7) for n in range(frames))


def write(path, data):
    with open(path, 'wb') as f:
        f.write(data)


def run(chdman, *args):
    subprocess.run([chdman, *args], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def sha256(path):
    with open(path, 'rb') as f:
        return hashlib.sha256(f.read()).hexdigest()


def main():
    chdman = sys.argv[1]
    os.makedirs(os.path.join(HERE, 'sheets'), exist_ok=True)
    expected = []
    with tempfile.TemporaryDirectory() as work:
        iso = os.path.join(work, 'in.iso')
        write(iso, dvd_image())
        for name, codecs, hunk in [
            ('dvd-default', 'lzma,zlib,huff,flac', 4096),
            ('dvd-lzma', 'lzma', 4096),
            ('dvd-zlib', 'zlib', 4096),
            ('dvd-huff', 'huff', 4096),
            ('dvd-flac', 'flac', 4096),
            ('dvd-zstd', 'zstd', 4096),
            ('dvd-none', 'none', 4096),
            ('dvd-flac-large-hunk', 'flac', 65536),
            ('dvd-all-large-hunk', 'lzma,zlib,huff,flac', 65536),
        ]:
            chd = os.path.join(HERE, name + '.chd')
            if os.path.exists(chd):
                os.remove(chd)
            run(chdman, 'createdvd', '-i', iso, '-o', chd, '-c', codecs, '-hs', str(hunk))
            out = os.path.join(work, name + '.iso')
            run(chdman, 'extractdvd', '-i', chd, '-o', out)
            expected.append(f'{name}.chd {sha256(out)}')

        audio = tone(RAW * 40, little=True)
        write(os.path.join(work, 'mode2.bin'), cd_data_track(40, 2))
        write(os.path.join(work, 'mode1.bin'), cd_data_track(30, 1))
        write(os.path.join(work, 'audio.bin'), audio)
        write(os.path.join(work, 'cooked.bin'), dvd_image()[:SECTOR * 40])
        write(os.path.join(work, 'mix.bin'), cd_data_track(41, 1) + audio + audio[:RAW * 10])
        sheets = {
            'single': 'FILE "mode2.bin" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n',
            'mode1': 'FILE "mode1.bin" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n',
            'cooked': 'FILE "cooked.bin" BINARY\n  TRACK 01 MODE1/2048\n    INDEX 01 00:00:00\n',
            'mix': 'FILE "mix.bin" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n'
                   '  TRACK 02 AUDIO\n    PREGAP 00:02:00\n    INDEX 01 00:00:41\n'
                   '  TRACK 03 AUDIO\n    INDEX 00 00:01:06\n    INDEX 01 00:01:11\n',
        }
        for sheet, text in sheets.items():
            write(os.path.join(work, sheet + '.cue'), text.encode())
        for name, sheet, codecs, hunk in [
            ('cd-default', 'single', 'cdlz,cdzl,cdfl', 19584),
            ('cd-cdlz', 'single', 'cdlz', 19584),
            ('cd-cdzl', 'single', 'cdzl', 19584),
            ('cd-cdzs', 'single', 'cdzs', 19584),
            ('cd-cdfl-audio', 'mix', 'cdfl', 19584),
            ('cd-mode1', 'mode1', 'cdlz,cdzl,cdfl', 19584),
            ('cd-cooked', 'cooked', 'cdlz,cdzl,cdfl', 19584),
            ('cd-mix', 'mix', 'cdlz,cdzl,cdfl', 19584),
            ('cd-large-hunk', 'mix', 'cdlz,cdzl,cdfl', 97920),
        ]:
            chd = os.path.join(HERE, name + '.chd')
            if os.path.exists(chd):
                os.remove(chd)
            run(chdman, 'createcd', '-i', os.path.join(work, sheet + '.cue'), '-o', chd, '-c', codecs, '-hs', str(hunk))
            cue = os.path.join(work, name + '.cue')
            bin_path = os.path.join(work, name + '.bin')
            run(chdman, 'extractcd', '-i', chd, '-o', cue, '-ob', bin_path)
            text = open(cue, 'rb').read().replace(os.path.basename(bin_path).encode(), b'out.bin')
            write(os.path.join(HERE, 'sheets', name + '.cue'), text)
            expected.append(f'{name}.chd {sha256(bin_path)}')

    write(os.path.join(HERE, 'expected.txt'), ('\n'.join(expected) + '\n').encode())


if __name__ == '__main__':
    main()
