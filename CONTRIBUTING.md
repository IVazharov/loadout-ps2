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

`VERSION` holds the version of both apps, following [Semantic Versioning](https://semver.org): `0.9.3` for a release, and between releases the next version with a pre-release suffix, such as `0.10.0-dev.1`. The .NET build and the ps2 Makefile read it as it is.

Work between releases goes on the branch `next`; `main` holds the latest release. Each commit that changes Loadout or Loadout Bay raises the suffix's number by one (`0.10.0-dev.1`, then `0.10.0-dev.2`), so every build reports its own version, and Connect offers a Loadout Bay built from another commit. Other commits keep it. Below 1.0.0, a new feature or a change that breaks compatibility raises the minor number, and a fix the patch number. The release drops the suffix.

A Windows build embeds `ps2/loadout-bay.elf` when it exists, to offer it to a PS2 that runs another version, so build the ELF first.

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

Add a line to [CHANGELOG.md](CHANGELOG.md) for anything a user would notice, under `## Unreleased` at the top. Internal changes do not need an entry.

## Releases

A release is built from a commit on `next` that drops the suffix from `VERSION`, such as from `0.10.0-dev.7` to `0.10.0`, and renames `## Unreleased` in `CHANGELOG.md` to `## <version> - <date>`, which supplies the notes. `main` then moves to that commit, and a maintainer pushes the tag `v<version>`, such as `v0.10.0`. The release workflow refuses a tag that does not match `VERSION` or has no changelog section, before it builds anything, so a `-dev` version is never released. It then builds and tests both apps, and creates a draft GitHub release with both downloads. A version below 1.0.0 is a prerelease.

The maintainer then logs in to SimplySign Desktop and runs `tools/sign-release.ps1 -Tag v<version>`, which signs `LoadoutPS2.exe` with a timestamp and replaces it in the draft's zip. Running it again with `-Publish` publishes the release.

## Commits and pull requests

A commit message starts with one summary sentence that ends with a period, then a short body that says why. Keep each commit to one change. A pull request says what changed, why, and how it was tested.
