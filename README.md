<p align="center">
  <img src="branding/emmaspace-readme.jpg" alt="Emma Space: disk space analyzer" width="100%">
</p>

# <img src="src/EmmaSpace.App/Assets/emmaspace.png" alt="" width="36" align="top"> Emma Space

[![CI](https://github.com/Alexxfromgit/EmmaSpace/actions/workflows/ci.yml/badge.svg)](https://github.com/Alexxfromgit/EmmaSpace/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/Alexxfromgit/EmmaSpace?sort=semver)](https://github.com/Alexxfromgit/EmmaSpace/releases/latest)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A fast disk space analyzer for Windows, with macOS and Linux engines in progress. It maps every
file on a drive or folder and shows what is big, old or duplicated, with a cushion treemap, a
folder tree and clean-up tools.

- **C++ engine** (`native/emmascan`): NTFS master file table reader and parallel directory
  walkers (Windows, macOS/Linux) behind a plain C ABI.
- **C# core** (`src/EmmaSpace.Core`): tree model, analyzers, duplicate finder, snapshots, export.
- **Avalonia UI** (`src/EmmaSpace.App`): MVVM desktop app on .NET 10, light and dark themes.

## Download

Get the latest version from [Releases](https://github.com/Alexxfromgit/EmmaSpace/releases/latest):

| Platform | Installer | Portable |
|---|---|---|
| Windows 10/11 (x64) | `EmmaSpace-win-x64-Setup.exe` | `EmmaSpace-win-x64-Portable.zip` |
| Windows 11 on ARM | `EmmaSpace-win-arm64-Setup.exe` | `EmmaSpace-win-arm64-Portable.zip` |
| macOS 12+ (Apple silicon) | `EmmaSpace-osx-arm64-Setup.pkg` | `EmmaSpace-osx-arm64-Portable.zip` |
| macOS 12+ (Intel) | `EmmaSpace-osx-x64-Setup.pkg` | `EmmaSpace-osx-x64-Portable.zip` |
| Linux (x64) | `.AppImage` (runs as is, no install; see below) | |

On Linux the AppImage adds itself to your applications menu when it starts, so `.emmasnap`
snapshots open in it with a double-click. It writes `com.alexxfromgit.emmaspace.desktop` to
`~/.local/share/applications`, `com.alexxfromgit.emmaspace.xml` to `~/.local/share/mime/packages`
and two icons under `~/.local/share/icons/hicolor/256x256`, and once the desktop has taken them in,
notes that in `~/.local/share/EmmaSpace/desktop-registration` (until then it tries again at each
start). Delete those files to undo it (they come back the next time the AppImage runs).

Installed copies update themselves: Emma Space checks for a new release shortly after it starts
and offers to restart into it (you can turn that off in the version menu in the status bar).
`SHA256SUMS.txt` on each release lists the checksums of every file. Until the builds are code
signed, Windows SmartScreen asks you to confirm ("More info" → "Run anyway") and macOS needs
right-click → Open the first time.

## Features

| Area | What you get |
|---|---|
| Scan | Drives or any folder; progress with live counts; cancel any time; links/junctions are not followed |
| Fast scan | As administrator, whole NTFS drives are read straight from the master file table (C: with 5.8 M entries in ~8 s instead of minutes) |
| Cloud files | OneDrive and other cloud placeholders count only what is stored on this PC (marked ☁); unpopulated cloud folders are not listed, so scanning never triggers downloads |
| Folder tree | Size, share of parent, size on disk, file count; huge folders page in 500 items at a time |
| Treemap | Squarified layout with cushion shading, colour by file type, labels, click to select, double-click to zoom, breadcrumb to go back |
| File types | Categories donut, every extension with totals, largest files of the selected type |
| Largest files | Top 1,000 files |
| Duplicates | Same size → hash of head and tail → full hash (xxHash128); only candidates are read |
| Clean-up | Files not modified/opened for 6 months–5 years; empty folder trees |
| Snapshots | Save a scan (`.emmasnap`, Brotli) and compare it later: what grew, shrank, appeared or vanished. Snapshot files have their own icon and open in Emma Space with a double-click (registered by the Windows installer, the macOS app bundle, and on Linux when the AppImage first runs); if Emma Space is already open, it asks before replacing what is on screen |
| Export | Self-contained HTML report; CSV of folders or of every file |
| Actions | Open, show in Explorer/Finder, copy path, Properties (Windows), move to Recycle Bin / Trash (asks first; on Windows the shell warns before anything would be deleted permanently; on macOS Finder's "Put Back" works) |

## Architecture

```
EmmaSpace.App   Avalonia views, view models, TreemapControl
      │
EmmaSpace.Core  FileTree (flat arrays) · analyzers · snapshots · export · scanners
      │  LibraryImport, zero-copy spans over native memory
emmascan.dll    C++20: MFT reader (raw volume) · parallel walker (GetFileInformationByHandleEx) · Recycle Bin (IFileOperation)
libemmascan     macOS/Linux: parallel walker (openat/fstatat, firmlink and hard-link de-duplication, online-only iCloud items never downloaded) · Trash (NSFileManager)
```

Lists use Avalonia 12.1's built-in `TableView` (MIT, part of `Avalonia.Controls`): resizable
columns, virtualized rows. The folder tree is a flattened list of visible rows in a `TableView`
whose first column draws the hierarchy. `FileTable` (`Controls/FileTable.axaml`) is the shared
file list. The old `Avalonia.Controls.DataGrid` package is deprecated, and `TreeDataGrid` needs a
paid Avalonia Pro licence, so neither is used.

The engine produces a **flat tree**: one 64-byte `es_node` per file or folder (parent, first
child, next sibling, sizes, times) and a single UTF-16 name pool. C# reads that memory directly
as `Span<EsNode>`, so no per-file objects or marshalling are needed. Directory sizes are rolled
up and siblings are sorted largest-first in C++.

If the native library is missing or the platform has no native backend yet, `DefaultScanner`
falls back to `ManagedScanner`, which produces the same format in pure .NET.

Scan modes (`es_scan_mode`): **Auto** uses the MFT for a whole NTFS drive when elevated and the
walker otherwise. The MFT always reads the entire table, so for a subfolder walking it is faster.
The "⚡ Fast scan" button restarts the app as administrator (`--scan <path>`) to get there.

Measured on this machine (full `C:\`, warm cache):

| Scanner | Entries | Time | Notes |
|---|---|---|---|
| MFT (elevated) | 5.8 M | ~8.5 s | hard links (617 k, mostly WinSxS) counted once |
| Native walker | 5.2 M | ~56 s | bound by per-directory open cost; thread count barely matters |
| Managed walker | 2.9 M¹ | ~50 s | ~3× the memory of the native walker |

¹ measured before cloud folders were walked. MFT and walker trees were cross-checked: directory
and file counts agree apart from files changing between passes, NTFS metafiles (`$MFT`, …) and
folders the walker cannot open.

## Build and run

Prerequisites (Windows): .NET SDK 10.0.4xx, Visual Studio 2026 with **Desktop development with C++**,
CMake ≥ 3.25.

```powershell
./build.ps1 -Test            # native + .NET build, all tests
dotnet run --project src/EmmaSpace.App
./build.ps1 -Publish         # self-contained build in artifacts/publish/win-x64
./build.ps1 -Pack            # Velopack Setup.exe, portable zip and update packages in artifacts/releases/win-x64
```

macOS / Linux: `./build.sh --test` (CMake ≥ 3.25, Ninja, a C++20 compiler and the .NET SDK).
On Ubuntu 24.04: `sudo apt-get install -y build-essential cmake ninja-build libicu74`.

CI (`.github/workflows/ci.yml`) builds and tests the engine and .NET code on Windows, macOS and
Linux for every pull request and push to `main`, cross-compiles Windows ARM64, and uploads Velopack
installers from the Windows jobs as build artifacts.

### Releases

Releases are automated with [Release Please](https://github.com/googleapis/release-please) and
[Velopack](https://velopack.io); nobody edits version numbers or tags by hand.

1. Pull requests are squash-merged and their titles follow
   [Conventional Commits](https://www.conventionalcommits.org): `feat: …` (minor), `fix: …`,
   `perf: …` or `deps: …` (patch), `feat!: …` or a `BREAKING CHANGE:` footer (breaking; a minor
   bump while we are on 0.x). `docs`, `ci`, `build`, `test`, `refactor` and `chore` are left out
   of the release notes. The `PR title` check enforces the format.
2. After each merge, Release Please keeps a `chore(main): release X.Y.Z` PR up to date. It bumps
   `<Version>` in `Directory.Build.props` and the CMake project version, and adds the release notes to
   [`CHANGELOG.md`](CHANGELOG.md).
3. Merging that PR tags `vX.Y.Z` and creates a draft release. `.github/workflows/release.yml` then
   builds and tests win-x64, win-arm64, osx-arm64, osx-x64 and linux-x64, packs each with Velopack
   (a delta update from the previous release too), uploads everything with `SHA256SUMS.txt` and
   publishes the release.

To rebuild the files of an existing release, run the *Release* workflow by hand with its tag.
Windows and macOS code signing switch on automatically once their secrets are configured.

To try an update locally, pack two versions into one folder with `vpk pack` and start the older
installed app with `EMMASPACE_UPDATE_FEED=<that folder>`; `EMMASPACE_GITHUB_TOKEN` lets it read the
releases of a private repository.

A plain `dotnet build` (or F5 in Rider) also builds the C++ engine through
`build/Native.targets`, as long as `cmake` is on `PATH`. Pass `-p:EmmaSkipNative=true` to skip it.

Tests:

```powershell
ctest --preset win-x64                         # C++ (Catch2)
dotnet test --project tests/EmmaSpace.Core.Tests
```

The Recycle Bin test is hidden because it leaves a file in the real bin:
`artifacts/native-build/win-x64/native/emmascan/tests/Release/emmascan_tests.exe "[trash]"`.

## Layout

```
native/emmascan/      C ABI header, node store, Windows walker + trash, POSIX stub, tests
src/EmmaSpace.Core/   Interop, Model, Scanning, Analysis, Snapshots, Export, Platform
src/EmmaSpace.App/    App, Views, ViewModels, Controls (treemap, donut, share bar), Services
tests/                xUnit v3 tests (Microsoft.Testing.Platform)
build/                Native.targets (CMake from MSBuild), make-icon.ps1, make-icns.py, stage-release.py
branding/             icon source art (make-icon.ps1 and make-icns.py build the app icons from it), README banner
```

## Roadmap

1. **Verify macOS** on a real Mac: the POSIX engine is verified on Linux (WSL Ubuntu 24.04: all
   tests pass, and a scan of `/usr` matches `du`/`find` exactly in bytes, files, folders and hard
   links), but the macOS-only parts (Trash, firmlink and `/System/Volumes` handling) are only
   compiled by CI. Then `getattrlistbulk` for faster enumeration, Full Disk Access guidance, and a
   signed, notarized `.app` via `vpk pack` on macOS.
2. **Code signing**: sign the Windows installer and sign and notarize the macOS app; the release
   workflow is ready for both.
3. MFT: handle a `$MFT` whose runlist spans extension records (currently falls back to the walker),
   overlap reads with parsing.
4. Multi-select delete, hard-link de-duplication in the Windows walker, treemap search and highlighting.

## Contributing and security

See [CONTRIBUTING.md](CONTRIBUTING.md). Please report vulnerabilities privately as described in
[SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE) © 2026 Alexander Rubtsov. Third-party components keep their own licenses: Avalonia,
CommunityToolkit.Mvvm, Velopack and Catch2 are MIT; System.IO.Hashing and the .NET runtime are MIT.
