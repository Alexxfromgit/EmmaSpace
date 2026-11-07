# Security policy

Emma Space reads whole drives (and, with "Fast scan", the raw NTFS volume as administrator), so
security reports are taken seriously.

## Reporting a vulnerability

Please **do not open a public issue**. Report it privately through
[GitHub's private vulnerability reporting](https://github.com/Alexxfromgit/EmmaSpace/security/advisories/new)
(Security tab → *Report a vulnerability*). Include the version, OS, steps to reproduce and the
impact you see.

You can expect an acknowledgement within a few days. Once a fix is released, the advisory is
published with credit to the reporter unless you prefer otherwise.

## Supported versions

Only the latest release receives fixes.

## Scope

Of particular interest:

- Anything that makes a scan modify, move or delete files, or follow links outside the scanned
  folder.
- Problems in the elevated MFT reader (`native/emmascan/src/win/mft_reader.cpp`,
  `native/emmascan/src/ntfs/`), for example malformed on-disk data causing memory corruption.
- Snapshot (`.emmasnap`) parsing of untrusted files.
- The installer and update mechanism.
