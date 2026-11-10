# Contributing to Emma Space

Thanks for helping! Bug reports, ideas and pull requests are all welcome.

## Getting started

You need the .NET 10 SDK (`global.json` pins the version), CMake ≥ 3.25 and a C++20 compiler:

- **Windows:** Visual Studio 2026 with *Desktop development with C++*.
- **macOS:** Xcode command line tools and Ninja (`brew install cmake ninja`).
- **Linux:** for example `sudo apt-get install -y build-essential cmake ninja-build libicu74 e2fsprogs` (mke2fs builds the ext4 test images).

```bash
./build.ps1 -Test          # Windows
./build.sh --test          # macOS / Linux
dotnet run --project src/EmmaSpace.App
```

See the [README](README.md) for the architecture and layout.

## Pull requests

- Keep each PR to one change and describe what it does and how you tested it.
- Title the PR as a [Conventional Commit](https://www.conventionalcommits.org): PRs are
  squash-merged, and the title becomes the commit that drives versioning and release notes.
  - `feat: add treemap search` → new feature, minor release
  - `fix: keep selection after rescan`, `perf: …`, `deps: …` → patch release
  - `feat!: …`, or a `BREAKING CHANGE:` line in the description → breaking change
  - `docs:`, `refactor:`, `test:`, `build:`, `ci:`, `chore:` → no release on their own, not in the notes

  Write the subject for users: it shows up as-is in the changelog and the in-app update notes.
- Add or update tests: C++ tests live in `native/emmascan/tests` (Catch2), .NET tests in
  `tests/EmmaSpace.Core.Tests` (xUnit v3). CI runs both on Windows, macOS and Linux.
- Follow the existing style; `.editorconfig` covers formatting. Builds should stay warning-free.
- The C ABI in `native/emmascan/include/emmascan/emmascan.h` is shared with C#
  (`src/EmmaSpace.Core/Interop`). Change both sides together and bump `ES_ABI_VERSION` when the
  layout changes.

## Safety rules for scanning code

Emma Space runs on people's real disks, so scanning must never change anything:

- Open files and folders read-only; never follow junctions, mount points or symlinks by default.
- Do not enumerate virtualized cloud folders (`FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS`): that makes
  OneDrive and other providers fetch data from the network.
- Deleting always goes to the Recycle Bin / Trash, after the user confirms.

## Reporting bugs

Use the issue templates. For security problems, follow [SECURITY.md](SECURITY.md) instead of
opening a public issue.
