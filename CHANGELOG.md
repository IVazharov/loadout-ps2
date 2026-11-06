# Changelog

Notable changes to Loadout and Loadout Bay. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Until 1.0.0, every release is a prerelease and behavior can change between minor versions. This changelog has a section for each release.

## Unreleased

### Changed

- A chd is now read as it's sent to the PS2. Loadout used to convert each chd to an iso, or a bin and cue, when you added it, and kept that copy, so adding a folder of chd files took a while and wrote all those games to your disk again. The Cache folder setting is gone with it.
- A damaged chd now shows when its install reaches the damage: the install stops, says the image could not be read, and Loadout Bay removes the partial copy. Adding a chd used to read it whole, so the damage showed then.
- When an install fails, is cancelled, or loses Loadout, Loadout Bay now removes the part of the game it had copied, and the app says so. It used to stay on the PS2 as an incomplete install, to be removed in Installed Games.
- A game with an incomplete install on the PS2, such as one cut off when the PS2 lost power, now reads Ready in the install list, with Incomplete under Installation, and installing it replaces that copy. It used to read Failed until you removed the copy in Installed Games.
- This Loadout needs the Loadout Bay from the same release. Connect offers the update, as before.

### Fixed

- When a game's image can't be read during an install, the app now says so. It used to say that the data connection to Loadout Bay was lost.
- A game cancelled just after its last byte was sent, which Loadout Bay then installed, now reads Installed. It used to read Cancelled, although the game was on the PS2.

## 0.9.2 - 2026-11-06

### Added

- The install list has a Size column, as Installed Games has, and its header sets the ready games' total against the PS2's free space, as in "3 IN QUEUE · 3 READY · 3.8 GB OF 17.9 GB FREE". It says when they don't fit. The free space is from the last Connect, less each game installed since.

### Changed

- The Windows app is now `LoadoutPS2.exe`. Windows took the old name, `Loadout.exe`, for a game, and on a system without Game Bar, such as Windows 10 LTSC, that showed a `ms-gamingoverlay` error at each start. To update, unzip over your old copy, which keeps your settings and caches, then delete `Loadout.exe`.
- The readme now says how far Loadout has been tested on Windows 10.
- The repository page now opens with screenshots of both apps. The readme in the zip stays text only.
- The Windows app is now signed, by Open Source Developer Ivan Vazharov. The certificate is new, so Windows may still warn you about it.

### Fixed

- The window now fits the screen. On a screen shorter than the window, such as 1080p at 150% scale, it opened with its title bar off the top of the screen, and a saved position could do the same after the resolution or scale changed.
- A long message in Game Details now wraps onto a second line, with the rest in its tooltip, instead of running under the Revert button.
- On Windows 10, the title bar now follows the theme as soon as you change it in Settings. It used to keep the old colour until the window lost focus or Loadout restarted.
- The install queue now follows a batch: the game being sent shows Installing, each finished game shows Installed, and the count of ready games goes down. Rows used to say Ready until the whole batch ended.
- If Loadout Bay stops answering after each game in a batch has its result, the app now reports those results, adding that Loadout Bay stopped answering. It used to say only "Lost the connection to Loadout Bay", or that the install had failed, and lose each game's result.
- A game with an incomplete install on the PS2, such as one whose copy failed the check or was cancelled, now reads Failed in the install list, with Incomplete under Installation and a note to remove it in Installed Games first. It used to read Ready again, and installing it again only skipped it.
- The Installed Games header now gives the unit of the disk's size, as in "21.9 GB FREE OF 40.0 GB".

## 0.9.1 - 2026-11-03

### Added

- Connect shows the covers the PS2 already has. On a new PC, it reads each cover the PC lacks from OPL's ART folder, so the list has its pictures without a download. This takes a few seconds the first time, and the list is on screen while it runs, though removing and editing games wait until it's done.
- The readme now lists what the PC and the PS2 need, including Windows and SQLite versions, and it comes with the Windows download.

### Fixed

- Loadout Bay refuses a partition name in OPL's `conf_hdd.cfg` that it can't use and says why, rather than writing to `+OPL`, where OPL would not look.
- Loadout Bay makes the `OPL` folder on a custom OPL partition when it isn't there yet.
- **Put Loadout Bay on the hard disk** says so when a build doesn't include Loadout Bay, instead of doing nothing. Release downloads always include it.

This is a prerelease, and the Windows app isn't signed yet, so Windows may warn you about it, and Smart App Control may block it. Please report problems on the [issues page](https://github.com/IVazharov/loadout-ps2/issues).

## 0.9.0 - 2026-11-03

First public release.

### Added

- Install PS2 games over the network onto a fat PS2's hard disk, in the layout Open PS2 Loader reads, from iso, bin and cue, or chd files, with a check that reads each game back, on by default.
- Installed Games lists the PS2's games, removes them, and edits a game's title, disc type, compatibility modes, and transfer mode.
- An Artwork page downloads covers and other artwork and sends what OPL's ART folder lacks.
- The Install page sends each game's artwork with it, on by default, and can give each game a virtual memory card in OPL's VMC folder.
- Find Bay finds Loadout Bay on the PC's networks.
- Loadout offers to update a Loadout Bay of another version over the network, and can put Loadout Bay on the hard disk as an OPL app.
- Loadout Bay has its own screen, with the hard disk's free space, and controller support, with network settings that can be changed on the console.
- Dark, light, and system themes, with Windows' contrast themes followed.
