# Open PS2 Loader's memory card groups

The titles that share one virtual memory card in Open PS2 Loader, such as the discs of one game or the games of one series, and the name OPL gives that card. Loadout names a new card the way OPL's game menu does, `<group>_<slot>`, so it finds a card that OPL made and OPL finds the cards that Loadout makes. A title in no group uses its own startup file name, such as `SLUS_202.97_0`.

- Source: [Open PS2 Loader](https://github.com/ps2homebrew/Open-PS2-Loader) `src/vmc_groups.c` at commit `1cb3a8eeed7218844796090eaed81e2bb1145712`.
- File: `vmc-groups.txt`, the same table as text: one group per line, its ID, then its title IDs. A title listed in two groups belongs to the first, as in OPL's `getGroupIdForTitleId`.
- Licence: AFL-3.0, in `LICENSE` (Open PS2 Loader's licence file).
