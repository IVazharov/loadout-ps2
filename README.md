# Loadout for PS2

Install PlayStation 2 games and artwork from a Windows PC, over the network.

Loadout has two parts:

- **Loadout for PS2** is the Windows app. It scans disc images on the PC and sends games and artwork to the PS2.
- **Loadout Bay for PS2** is the PS2 app. It receives games and artwork and writes them to the PS2's internal hard disk, in the layout Open PS2 Loader (OPL) reads.

Both parts share one version number.

## Requirements

- Windows 10 or 11 with the .NET 10 Desktop Runtime.
- A fat PS2 (SCPH-3000x to SCPH-5000x) with the official network adapter or a compatible one, and an internal hard disk. Slim models have no internal hard disk, so they cannot receive games.
- The PC and the PS2 on the same network.

## Get started

1. Download both zips from the releases page.
2. Copy `loadout-bay.elf` to a memory card or USB drive and start it from wLaunchELF. Its screen shows the PS2's address.
3. Unzip Loadout anywhere you can write and run `Loadout.exe`. It keeps its settings and caches next to the executable.
4. In Settings, press **Find Bay** or enter the address from the TV.
5. On **Install**, add iso, bin and cue, or chd files, then press **Install on PS2**.
6. Start Open PS2 Loader on the PS2. It lists the games from its hard disk section.

Artwork, game settings, and memory cards go in the partition OPL makes the first time it lists games from the hard disk, so run OPL's hard disk list once first.

## What Loadout downloads

Loadout goes online for two things only, and neither is part of Loadout or under its licence:

- Game titles and settings data come from the [PS2-OPL-CFG-Database](https://github.com/Thecat1080/PS2-OPL-CFG-Database) by Thecat1080.
- Artwork comes from the [psx-ps2-opl-art-database](https://github.com/Luden02/psx-ps2-opl-art-database) by Luden02, when you press **Download artwork**. The images belong to their owners.

## Downloads

Each release has two downloads:

- `Loadout-PS2-<version>-win-x64.zip` holds `Loadout.exe` and the licence texts.
- `Loadout-Bay-PS2-<version>.zip` holds `loadout-bay.elf`, an example network settings file, a short readme, and the licence texts.

## Docs

- [Loadout Bay](ps2/README.md): starting it, and its network settings.
- [Protocol](docs/bay-protocol.md): how the two parts talk.
- [Speed test](docs/speed-test.md): measuring the network and the hard disk.
- [Contributing](CONTRIBUTING.md): building and testing both apps.

## Licence

The source code written for Loadout and Loadout Bay is under the MIT licence: see [LICENSE](LICENSE). The downloads also contain other projects' code, under their own licences: [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) lists them, and comes with both downloads. By using or redistributing the binaries you accept those licences. Code taken from other projects is in `ps2/third_party/` and `src/Loadout.Core/ThirdParty/`. The Barlow fonts in `ps2/assets/fonts` and `src/Loadout.App/Assets/Fonts`, and `ps2/src/font_data.c`, which is rendered from them, are under the SIL Open Font License 1.1.

Maintained by Ivan Vazharov.

## Notice

Loadout works with disc images you made from discs you own. It does not include or download games, BIOS files, or Sony code, and the project gives no help finding them. PlayStation and PS2 are trademarks of Sony Interactive Entertainment; this project is independent, and not affiliated with or endorsed by Sony.
