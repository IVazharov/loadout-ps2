# Contributing

This file covers how to build both apps, what tests may do, the rules for writes to the PS2's hard disk, and the style for code and docs.

## Build and test the Windows app

Requires the .NET 10 SDK on Windows:

```powershell
dotnet build Loadout.slnx -c Release
dotnet test Loadout.slnx -c Release --no-build
```

Warnings are errors. The build runs the .NET recommended analyzers and the style rules in `.editorconfig`, so a pull request with a new warning fails CI. Fix the warning. Do not suppress it without a reason in the same change.

## Build Loadout Bay

Loadout Bay builds with the ps2dev toolchain. The simplest way is the ps2dev Docker image that CI uses:

```bash
docker run --rm -v "$PWD:/src" -w /src ps2dev/ps2dev:v2.0.0 sh -c "apk add --no-cache build-base git && git config --global --add safe.directory /src && make -C ps2"
```

The result is `ps2/loadout-bay.elf`.

## Version

`VERSION` holds the major.minor of both apps. The .NET build and the ps2 Makefile read it and add the build number, the count of commits after the first, so they need the full history: a shallow clone counts fewer, and a build outside git uses 0. A Windows build embeds `ps2/loadout-bay.elf` when it exists, to offer it to a PS2 that runs another version, so build the ELF first.

## Tests never dial a console

Tests run against fakes that listen on loopback. Do not point a test at a real address.

A test name says what happens, in PascalCase, for example `ARefusedInstallSendsNoData`.

## Writes to the PS2's hard disk

A wrong write can corrupt the PS2's hard disk, so:

- Every write path is built and tested in PCSX2 against a virtual hard disk first.
- The first console test of a write uses one small game that can be reinstalled.
- Loadout Bay writes only `PP.<DiscID>.HDL.*` partitions, files under `ART`, `CFG`, `VMC` and `APPS/Loadout-Bay` in OPL's `+OPL` partition, its own ELF and settings where it was started from, and a temporary partition for the disk speed test. It never touches other partitions.
- A delete is sent only for a `PP.<DiscID>.HDL.*` partition and only after the user confirms.
- Loadout Bay does not write OSD resources.

A pull request that changes a write path says how it was tested in PCSX2 and, if it was, on which console.

## Code from other projects

HDLGameInstaller has no licence. Its source was read to learn the on-disk format and the layout of its games. Facts about the format, such as partition sizes and header fields, may be used. Its code may not be copied. Do not copy code from wLaunchELF or ps2ftpd, or from any GPL project.

Code from ps2sdk, Neutrino, or Open PS2 Loader may be reused. It goes in `ps2/third_party/<name>/` with its licence file and the version it came from; code or data that Loadout itself uses goes in `src/Loadout.Core/ThirdParty/<name>/` the same way. Add anything a binary then contains to `THIRD-PARTY-NOTICES.txt`.

Do not commit game images, saves, BIOS files, virtual hard disks, or artwork.

## Docs and changelog

Docs use short, plain, declarative sentences. They describe the apps as they are now. History belongs in the changelog and git, not in the docs.

Add a line to [CHANGELOG.md](CHANGELOG.md) for anything a user would notice, under the newest section at the top. Internal changes do not need an entry.

## Releases

A release is built from a commit on `main`. Its version is `VERSION` (major.minor), then the build number. A maintainer makes sure `CHANGELOG.md` has a `## <major.minor> - <date>` section, which supplies the notes, and pushes the tag `v<major.minor>.<build>`, such as `v0.9.1`. The release workflow refuses a tag that does not match the commit's version, builds and tests both apps, and creates the GitHub release with both downloads. A version below 1.0.0 is a prerelease.

## Commits and pull requests

A commit message starts with one summary sentence that ends with a period, then a short body that says why. Keep each commit to one change. A pull request says what changed, why, and how it was tested.
