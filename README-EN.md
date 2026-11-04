[简体中文](README.md) | **English** | [繁體中文](README-ZH_TW.md)

<div align="center">

<img src="Plain Craft Launcher 2/Images/icon.png" alt="Logo" width="96" height="96">

# PCL SUPER

A personal build based on [PCL-CE](https://github.com/PCL-Community/PCL-CE), with features ported from [PCL-Living-Fork](https://github.com/DDZmumo/PCL-Living-Fork).

**Pre-release**: cross-compiled on Linux with 0 errors and 415 migration tests passing; not yet run on a real Windows machine.

</div>

## What this is

PCL SUPER is not a separate launcher. Everything that overlaps with CE (UI, downloader, instance pages, crash analysis) stays as CE. LF's behaviour is reimplemented on top of CE's types and resource model, so the two coexist instead of replacing each other.

Baseline: PCL-CE `dev@16207f9e`; port source: LF `main` + `migration/csharp-modern-dotnet`.

## Implemented

- **Instance & environment**: restore points / snapshots, automatic snapshot before resource updates, instance clone and test-copy/migration
- **Resources & dependencies**: dependency inspection and launch gate, dependency auto-fill with transaction rollback, dependency graph, keybinding editor
- **Modpacks**: update from local `.zip` / `.mrpack`, direct link, or an author-supplied HTTPS manifest; diff preview, transactional commit, rollback, pre-update restore point; modpack mode
- **Saves**: "continue last world" on the home page, map archive import with validation and duplicate-name suffixing
- **CE adjustments**: removed region-based download restrictions, saves isolated from export by default

## Not included

- Hash-based platform lookup, `ResourceSearcher/Project/Version`, and platform-side update discovery
- All of these need platform APIs and credentials, currently deferred; no platform API is integrated

The author manifest accepts HTTPS static JSON only, with required `version` and `package` fields. See section 6 of [`docs/Living-Port-Matrix.md`](docs/Living-Port-Matrix.md).

## Acceptance status

All UI changes have only been cross-compiled on Linux and **have not been run on Windows**.
The item-by-item checklist is in [`docs/Windows-Acceptance.md`](docs/Windows-Acceptance.md).

## Build

Requires the .NET 10 SDK.

```bash
dotnet build "Plain Craft Launcher 2.slnx" -c Release
dotnet PCL.Living.Features.Tests/bin/Release/net10.0/PCL.Living.Features.Tests.dll
```

Windows x64 framework-dependent publish:

```bash
dotnet publish "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" -c Release -r win-x64 --self-contained false -o publish
```

Running requires the .NET 10 Desktop Runtime (x64).

## Layout

| Directory | Contents |
|---|---|
| `PCL.Core` / `PCL.Core.Wpf` | CE core and WPF foundation |
| `PCL.Launcher.Core` | Launch, instances, dependency auto-fill |
| `PCL.Living.Features` | Engines ported from LF (net10.0, no WPF) |
| `PCL.Living.Features.Tests` | 415 migration tests |
| `Plain Craft Launcher 2` | Application and UI |
| `docs` | Port matrix, acceptance checklist |

## License and provenance

- `Plain Craft Launcher 2/`: PCL Limited Distribution License, see [`Plain Craft Launcher 2/LICENCE`](Plain%20Craft%20Launcher%202/LICENCE).
  It only permits storing and redistributing unmodified copies; this build modifies that directory, so it **must not be distributed publicly**
- Other directories: Apache-2.0, see [`LICENSE`](LICENSE)
- Ported LF code and attribution: see [`NOTICE-LIVING.md`](NOTICE-LIVING.md)
- Upstream: [PCL](https://github.com/Meloong-Git/PCL) | [PCL-CE](https://github.com/PCL-Community/PCL-CE) | [PCL-Living-Fork](https://github.com/DDZmumo/PCL-Living-Fork)

This is a personal derivative and does not represent upstream. Do not report issues with this build to upstream repositories. This repository is a private preview and is not cleared for public distribution.
