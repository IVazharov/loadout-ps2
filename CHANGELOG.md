# Changelog

Notable changes to Loadout and Loadout Bay. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Until 1.0.0, every release is a prerelease and behavior can change between minor versions. This changelog has a section for each release.

## Unreleased

### Fixed

- Games that other tools installed under their own partition names, such as `PP.SLES-53886..BLACK`, couldn't be changed or removed: Installed Games listed them, but greyed out Remove and called them incomplete installs. Loadout and Loadout Bay now take any game partition named `PP.<disc ID>.` and something after it, as long as its header is a game's. Loadout Bay still names the games it installs `PP.<disc ID>.HDL.<title>`.
- Installed Games says why a game can't be changed: an incomplete install, a header Loadout Bay couldn't read, or a partition name it doesn't know, instead of calling them all incomplete installs.

### Removed

- The licence files that Loadout 0.10.0 and older kept beside the program (`LICENSE`, `THIRD-PARTY-NOTICES.txt` and the `third_party` folder) are deleted when Loadout starts: an update to 0.11.0 left them behind, outdated, and every licence text is on the About page. Only files exactly as those versions shipped them go; anything else in the folder stays.

## 0.11.0 - 2026-11-18

Loadout now backs up PS2 discs. Put an original PS2 DVD or CD in a drive on your PC, and Loadout copies it, checks it against Redump's list of verified discs, and keeps it as a chd or as a bin and cue. A scratched disc is read again where it's weak, and a copy can go straight to the PS2. Installs also show their check as it runs, every list has a right-click menu and keys, and Loadout Bay's screen shows the free space in OPL's partition.

New to Loadout? `Loadout-PS2-0.11.0-win-x64.zip` is the Windows app, and all that backing up needs. `Loadout-Bay-PS2-0.11.0.zip` is for installing games on a PS2: Get started, in the readme, has the steps. Loadout 0.10.0 offers this version by itself when it starts, and its next Connect updates Loadout Bay too.

### Added

- Back up discs (Ctrl+3) copies an original PS2 disc in a disc drive on this PC to an image here, checks it against Redump's list of verified discs, and names a copy that matches as Redump does. A scratched disc is read in passes that go back to its weak areas in smaller pieces and let the drive rest after each failed read. The copy never gives up by itself: it can be stopped at any time and resumed later, after cleaning the disc or in another drive. A disc map shows how each part of the disc was read, as a bar from the centre to the edge and as rings on a picture of the disc. Each copy keeps its report, its hashes and its map beside it.
- A CD is backed up as Redump keeps it: its raw sectors in a .bin, with a .cue, so the copy can be checked against Redump and installed from the Install page like any bin and cue.
- Pause to clean the disc, while a backup runs, stops the copy, opens the tray and shows where the disc has trouble, circled on a picture of it. Put the disc back cleaned and the copy goes on by itself; a small cloth then marks each cleaned ring, and its hover text says what came back there. While a backup runs, Windows doesn't open AutoPlay or Explorer for a disc put in that drive.
- Back up discs shows the drive's own read speed for the disc in it, such as 10x–24x for a CD, and while copying, the speed it reads at, such as 3.8x of 8x.
- Also install on PS2, on Back up discs, installs each disc on the PS2 as it is copied. If the copy meets a weak area, that install is cancelled and the finished copy is installed instead. The disc map's markers say which is which: DRIVE over it where the drive reads, PS2 under it where the install has got, and a green CHECK in its place while Loadout Bay checks it.
- Back up discs has its own install ticks, under Installs from this page: each game's artwork, a memory card for each game with its size, and the check. They are kept apart from the Install page's, and both pages remember theirs.
- While Loadout Bay checks a game, Loadout shows how far it has read the game back, its speed and the time left. Skip the check keeps the game on the PS2 without the rest of its check, and Cancel install removes it. This needs this release's Loadout Bay; an older one checks as before.
- Install on PS2, on Back up discs, installs the copy picked in the list. When a copy ends, it is the one picked, so Show report and Install on PS2 act on it.
- Delete, on Back up discs, or the Delete key in its list, sends the picked copies to the Recycle Bin with their reports, hashes and maps, after one question. Ctrl-click or Shift-click picks several. Games on the PS2 are not touched.
- Closing Loadout while a disc is backed up, or while a game is installed on the PS2, asks first and says what closing would stop. A backup goes on later with Resume this disc.
- When a copy stops on an error, the page and the notification say why in plain words, such as a backup folder Loadout may not write in, a full drive, or a disc drive that was unplugged, and that Resume this disc goes on from there. The report keeps Windows' own message.
- Back up again asks whether to keep both copies or replace the earlier one. Replace removes the earlier copy, to the Recycle Bin, only once the new one is complete; an earlier copy that matches Redump stays when the new one doesn't.
- While Loadout is behind other windows, Back up discs tells you in a Windows notification when a disc is backed up, when it needs cleaning to go on, and when a copy stops on an error; the taskbar button shows the copy's progress, turns yellow when it needs you and red when it stopped, and flashes until you come back.
- After a copy, Details opens its report: every pass, failed read, rest and cleaning. Save report… keeps a copy of a backup's report where you choose, and the Logs page has one line per disc, such as "99.3% on the first pass, 2 weak areas, 2 cleanings, matches Redump".
- About credits Redump's list of verified PS2 discs, which Back up discs checks copies against.
- Keep going, on Back up discs, is for a collection: a backed-up disc comes out of the drive, and the next PS2 disc put in is backed up by itself.
- Settings has a Backups section with the folder the copies go to. The first backup asks for it, suggesting Documents\PS2 Backups.
- Install has Back up a disc…, which opens Back up discs.
- Back up discs keeps a DVD's copy as a chd, about 40% smaller, and a CD's as a bin and cue, as Redump keeps it; Keep a DVD as and Keep a CD as, on the page, choose each. A copy is made as an iso or bin and checked against Redump as before. One kept as a chd is then compressed, beside the next disc's copy, and read back whole to be sure it is the same; only then is its iso or bin deleted, and only once no install on the PS2 still reads it. Compressing that closing Loadout stopped starts again next time. Before a copy that is to become a chd, the page asks when the drive may not have room for both at once.
- Convert to CHD, on Back up discs, compresses copies kept as an iso or a bin and cue; once a chd reads back the same, its iso or bin goes to the Recycle Bin. Convert to ISO/BIN turns copies kept as chds back into the iso, or the CD's bin and cue, they were made from: each is read back and compared with the copy's hashes, the ones Redump's check saw, before its chd goes to the Recycle Bin. The list's Kept as column says how each copy is kept, and Size is what it takes on the drive.
- Convert to CHD, Convert to ISO/BIN and Install on PS2, on Back up discs, take every copy picked with Ctrl or Shift, one after another. Picked copies one of them can't take, such as a copy kept that way already or an incomplete one, are named in a question first. When the drive may not hold what the conversions need, none starts, and the page says how much room they take.
- Changing Keep a DVD as or Keep a CD as offers to convert the copies already kept the other way.
- Every list has a right-click menu with what can be done with its picked rows, and the menu key and Shift+F10 open it too: on Back up discs, Show report, Save report…, Show in folder, both conversions, Install on PS2 and Delete; on Install, Show in folder and Remove; on Installed Games, Give memory cards… and Remove…; on Artwork, Download artwork and Send to PS2. Each has Select all. A right-click on a column header still sorts and sizes the columns.
- Keys for the lists: Ctrl+A picks every row of the page's list and Esc none, wherever the keyboard is on the page; Ctrl+C copies the picked rows as text, the column names first, for a post or a spreadsheet; Enter opens a row (a copy's report on Back up discs, its file in Explorer on Install, the game's title on Installed Games); Del removes on Install and Installed Games, as it deletes on Back up discs. F5 reads the list again: from Loadout Bay on Installed Games and Artwork, from the backup folder on Back up discs. Ctrl+O is Add files… on Install; on Installed Games, F2 goes to the picked game's title and Ctrl+S saves it.
- Artwork's Download artwork and Send to PS2 act on the games picked in its list, and say how many, such as Send to PS2 (3); with none picked they act on every game, as before. Open folder and Show in folder, on Back up discs, pick each picked copy's files in Explorer, a chd too.
- Settings has Go on with compressing and converting when Loadout starts, on from the start: conversions that closing Loadout stopped or left waiting go on by themselves, one at a time. Turned off, they wait until you convert them.
- Loadout writes chd files itself, as chdman does: each part in LZMA, zlib or Huffman, whichever is smallest, so PCSX2 and other emulators read them. The LZMA encoder is ported from Igor Pavlov's LZMA SDK, which is in the public domain; About says so.

### Changed

- A click on a list's empty space, under its rows, unselects everything, as in Explorer.
- Check each game after installing moved from Settings to the Install page, beside its artwork and memory card ticks. A check you had turned off stays off, on both pages. The Install page also remembers its artwork and memory card ticks now.
- The Windows download holds only `LoadoutPS2.exe` and its readme: every licence text, the fonts' and the chd reader's included, is on the About page. Loadout Bay's download keeps its licence files.
- Backup is the third page, after Install, so Artwork, Settings, Logs and About are now Ctrl+4 to Ctrl+7.
- On a drive without a Recycle Bin, such as a USB stick or a network share, what Back up discs removes (with Delete, Back up again's Replace, or once a conversion reads back the same) is deleted for good, as Windows does there. Its questions now say so before you confirm, and its reports after, instead of naming the Recycle Bin.
- Back up this disc says when the copy itself can't fit on the backup folder's drive, and how much room it takes, instead of starting a copy that stops at once on a full drive.
- Loadout Bay's screen shows the free space in OPL's partition, such as "3.8 of 128 MB", where the last speed test was: artwork, game settings, memory cards and Loadout Bay itself go there, and it is often far smaller than the hard disk. It turns amber below 8 MB, when another memory card no longer fits, and says "Not made yet" until OPL has made the partition. A speed test's result still shows on the status line.
- Loadout Bay is built with a newer ps2sdk, of 2026-11-16, which has its own fix for the clock fault that made Loadout Bay stop answering after about 36 minutes, so Loadout Bay no longer needs its own. Its hard disk, network and USB drivers come from the same ps2sdk.

### Fixed

- A list shown again with a page, such as Back up discs' after another page, showed its picked row while the page's buttons for it stayed off until another row was picked.
- Cancel, during an install's check, did nothing: the game was installed once the check ended. With this release's Loadout Bay, it now cancels the install, and Loadout Bay removes the game.
- When OPL's partition on the PS2 was too full to save a new Loadout Bay, Connect said Loadout Bay "wasn't updated" because "the game does not fit" on the hard disk, while the new one ran until the PS2 was switched off and the old one came back. Connect now checks the room first: when the new one can't be saved, nothing is sent, and the page says how much room it needs and how much is free. A new Loadout Bay that finds no room beside the old one deletes the old one's backup, `loadout-bay.elf.bak`, before it saves itself, so an update fits where the backup did. A save that fails anyway says why, and that the PS2 starts the old one next time.
- A file that Loadout Bay could not finish writing in OPL's partition, such as a memory card on a full partition, stayed there half written and kept its room. Loadout Bay now deletes it, and Connect deletes the ones earlier versions left. Artwork or a memory card that finds no room is told as OPL's partition being full, not as a game that doesn't fit on the hard disk.

## 0.10.0 - 2026-11-11

### Added

- Installed Games has a Memory card column: the size of the card in slot 1 of each game's OPL settings, "shared" when other games use the same card, or Missing when the card the settings name is not on the PS2. Its tooltip names the card and the games that share it.
- Give memory cards gives the selected games a card at once, from 8 to 64 MB, and a new card in place of a missing one. A card already on the PS2 is used as it is, with its saves, and never replaced. Before anything is written, it lists what each game gets and checks that the new cards fit in OPL's partition.
- An About page (Ctrl+6) shows Loadout's version and author, links to the GitHub page, the issues page and the changelog, where the game data and the artwork come from, and the licence texts. Its Check for updates asks GitHub whether a newer Loadout is out.
- Loadout checks GitHub for a newer version when it starts, and when one is out it shows what's new. Update now downloads it, checks that it is signed by its publisher and is the version offered, replaces Loadout's files, and restarts Loadout as the new version. Settings stay as they are, and a Loadout that was connected to Loadout Bay connects again. The check can be turned off in Settings, under Updates, or in the update dialog itself; About's Check for updates works at any time.
- With the update check on, Connect sends this Loadout's Loadout Bay without asking when the one on the PS2 is older. A newer one is still only replaced when you say so.

### Changed

- Give each game a memory card, on the Install page, now also makes a new card when the one a game's OPL settings name is missing. It used to leave that game without a card.
- In Installed Games, Partition is now the last column, so the others fit at the window's usual width.
- This Loadout needs the Loadout Bay from the same release, which tells it the free space in OPL's partition. Connect offers the update, as before.
- The readme now says how to give OPL a bigger partition than the 128 MB one it makes itself, so it has room for more memory cards: before OPL makes its own, or later by moving OPL to a new one with its saves.

### Fixed

- Loadout Bay no longer stops answering after it has run for about 36 minutes. From then on, a reply that lost a network frame on its way was never sent again, so Connect timed out on the game list ("did not answer within 30 seconds") until Loadout Bay was started again. The fault was in the clock that the PS2's TCP/IP stack, from ps2sdk, takes its time from.

## 0.9.3 - 2026-11-06

### Changed

- A chd is now read as it's sent to the PS2. Loadout used to convert each chd to an iso, or a bin and cue, when you added it, and kept that copy, so adding a folder of chd files took a while and wrote all those games to your disk again. The Cache folder setting is gone with it.
- A damaged chd now shows when its install reaches the damage: the install stops, says the image could not be read, and Loadout Bay removes the partial copy. Adding a chd used to read it whole, so the damage showed then.
- When an install fails, is cancelled, or loses Loadout, Loadout Bay now removes the part of the game it had copied, and the app says so. It used to stay on the PS2 as an incomplete install, to be removed in Installed Games.
- A game with an incomplete install on the PS2, such as one cut off when the PS2 lost power, now reads Ready in the install list, with Incomplete under Installation, and installing it replaces that copy. It used to read Failed until you removed the copy in Installed Games.
- This Loadout needs the Loadout Bay from the same release. Connect offers the update, as before.

### Fixed

- Loadout now starts on a Windows 10 without its updates on a recent processor (Intel 11th generation, AMD Zen 3 or later). There, it used to close at once without a word, because .NET stopped it: that Windows doesn't fully support the processor's stack protection (CET), which Loadout no longer asks for.
- When a game's image can't be read during an install, the app now says so. It used to say that the data connection to Loadout Bay was lost.
- A game cancelled just after its last byte was sent, which Loadout Bay then installed, now reads Installed. It used to read Cancelled, although the game was on the PS2.
- When a game fails its check, the install's message now says that it failed. It used to say that the game was skipped, although it had been sent.

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
