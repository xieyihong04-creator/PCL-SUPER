[简体中文](README.md) | **English** | [繁體中文](README-ZH_TW.md)

<div align="center">

<img src="Plain Craft Launcher 2/Images/icon.png" alt="Logo" width="96" height="96">

# PCL SUPER

A personal build based on [PCL-CE](https://github.com/PCL-Community/PCL-CE), with features ported from [PCL-Living-Fork](https://github.com/DDZmumo/PCL-Living-Fork).

**Status**: validated on real Windows x64 hardware with defects fixed; current version 2.15.2. 464 of 469 migration tests pass (the 5 failures are caused by non-admin restrictions on creating symbolic links).

</div>

## What this is

PCL SUPER is not a separate launcher. Everything that overlaps with CE (UI, downloader, instance pages, crash analysis) stays as CE. LF's behaviour is reimplemented on top of CE's types and resource model, so the two coexist instead of replacing each other.

Baseline: PCL-CE `dev@16207f9e`; port source: LF `main` + `migration/csharp-modern-dotnet`.

## Implemented

- **Instance & environment**: restore points / snapshots, automatic snapshot before resource updates, instance clone and test-copy/migration
- **Resources & dependencies**: dependency inspection and launch gate, dependency auto-fill with transaction rollback, dependency graph, keybinding editor
- **Download pages**: version and loader filters follow the selected instance; mods, resource packs and shaders are marked "Installed" in both the download lists and the detail page (file hashes matched against Modrinth / CurseForge projects)
- **Modpacks**: update from local `.zip` / `.mrpack`, direct link, or an author-supplied HTTPS manifest; diff preview, transactional commit, rollback, pre-update restore point; modpack mode
- **Saves**: "continue last world" on the home page, map archive import with validation and duplicate-name suffixing
- **Launcher**: self-update from this repository's GitHub Releases, verified against the release asset's SHA256 `digest`; update, announcement and error prompts are deferred while the launcher is not in the foreground or a game is launching/running, and shown once you return
- **CE adjustments**: removed region-based download restrictions, saves isolated from export by default

## Not included

- Full online resource search (`ResourceSearcher/Project/Version`) and platform-side resource update discovery
- Installed-state detection does batch-query Modrinth / CurseForge for project IDs by file hash; launcher updates only query this repository's GitHub Releases

The author manifest accepts HTTPS static JSON only, with required `version` and `package` fields. See section 6 of [`docs/Living-Port-Matrix.md`](docs/Living-Port-Matrix.md).

## Acceptance status

Real Windows x64 acceptance is complete; the defects found were fixed and are part of the current build.
The item-by-item checklist is in [`docs/Windows-Acceptance.md`](docs/Windows-Acceptance.md);
the migration tests live in `PCL.Living.Features.Tests` (469 tests, 5 of them blocked by non-admin symlink permissions).

## Build

Requires the .NET 10 SDK.

```bash
dotnet build "Plain Craft Launcher 2.slnx" -c Release
dotnet run --project PCL.Living.Features.Tests
```

Windows x64 self-contained single-file publish (no .NET runtime required):

```bash
dotnet publish "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" -c Release -p:Platform=x64 -o publish
```

## Publishing a release

1. Update `version.base` in `Plain Craft Launcher 2/metadata.json` (and the csproj version if needed).
2. Make sure `gh` is authenticated, then run:

   ```powershell
   powershell -NoProfile -File release.ps1 -Version 2.15.2
   ```

3. The script republishes the win-x64 single file, renames it to `PCL-SUPER-<version>-win-x64.exe` and creates a GitHub Release.

The launcher self-updates from this repository's Releases: the stable channel uses the newest stable release, the beta channel the newest prerelease.
The asset must be named `PCL-SUPER-<version>-win-x64.exe`; SHA256 is taken from the release asset's `digest`.

## Layout

| Directory | Contents |
|---|---|
| `PCL.Core` / `PCL.Core.Wpf` | CE core and WPF foundation |
| `PCL.Launcher.Core` | Launch, instances, dependency auto-fill |
| `PCL.Living.Features` | Engines ported from LF (net10.0, no WPF) |
| `PCL.Living.Features.Tests` | 469 migration tests |
| `Plain Craft Launcher 2` | Application and UI |
| `docs` | Port matrix, acceptance checklist |

## License and provenance

- `Plain Craft Launcher 2/`: PCL Limited Distribution License, see [`Plain Craft Launcher 2/LICENCE`](Plain%20Craft%20Launcher%202/LICENCE).
  It only permits storing and redistributing unmodified copies; this build modifies that directory, so the distribution scope of the modified build is limited by the upstream license — verify compliance yourself
- Other directories: Apache-2.0, see [`LICENSE`](LICENSE)
- Ported LF code and attribution: see [`NOTICE-LIVING.md`](NOTICE-LIVING.md)
- Upstream: [PCL](https://github.com/Meloong-Git/PCL) | [PCL-CE](https://github.com/PCL-Community/PCL-CE) | [PCL-Living-Fork](https://github.com/DDZmumo/PCL-Living-Fork)

This repository hosts the source and self-update releases of a personal build. It does not represent upstream; do not report issues with this build to upstream repositories.
