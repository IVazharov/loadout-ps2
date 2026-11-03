# Loadout Bay

Loadout Bay is the PS2 side of Loadout. It runs on a fat PS2 with a network adapter and an internal hard disk.

## Start it

1. Copy `loadout-bay.elf` to a memory card or USB drive.
2. On the PS2, open wLaunchELF and start `loadout-bay.elf`.

The screen shows what Loadout Bay is doing, the console's address and network settings, the hard disk, and the last speed test.

Press Triangle for the network settings: DHCP or a fixed address, set with the controller. Save writes `loadout-bay.cfg` next to `loadout-bay.elf` and uses the new settings at once; the screen shows the address to give Loadout on the PC.

Press START to leave Loadout Bay. It restarts the PS2 into its main menu, or FreeMcBoot's or Free HDBoot's when the PS2 boots one. While a game installs or a speed test runs, the controls wait until it is done.

### From the hard disk

Once Loadout Bay runs, Loadout's **Put Loadout Bay on the hard disk…** button (on Installed Games) copies it into `APPS/Loadout-Bay` in OPL's partition, with a `title.cfg`. OPL then lists it in its Apps menu when Settings → Applications Start Mode is on, and the USB drive is no longer needed. A `loadout-bay.cfg` for a fixed address goes in the same folder; the network settings on the console (Triangle) save it there.

## Network settings

Without a settings file, Loadout Bay gets its address from DHCP. The network settings on the console (Triangle) write the file for you. To write it by hand instead, copy `loadout-bay.example.cfg` next to `loadout-bay.elf`, rename it to `loadout-bay.cfg`, and edit it:

```
dhcp=off
ip=192.168.1.50
netmask=255.255.255.0
gateway=192.168.1.1
```

To leave for another program instead of the PS2's menu, name its ELF in an `exit=` line, such as `exit=mc0:/APPS/OPNPS2LD.ELF`, or `exit=hdd0:+OPL:pfs:/APPS/OPL/OPNPS2LD.ELF` for an ELF on the hard disk. If it cannot be started, Exit returns to the PS2's menu.

With `dhcp=on`, the addresses are ignored. A file without a `dhcp` line uses the fixed address. Lines starting with `#` are comments. If the file has a mistake, the screen names the line and Loadout Bay uses DHCP.

## Licences

The source code written for Loadout Bay is under the MIT licence, in `LICENSE`. The ELF also contains other projects' code under their own licences: see `THIRD-PARTY-NOTICES.txt` and the `third_party` folder of this download.
