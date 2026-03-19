# Third-Party Notices

## Inno Setup

This project uses Inno Setup to build the Windows installer package (`Setup.exe`).

- Website: https://jrsoftware.org/isinfo.php
- Download: https://jrsoftware.org/isdl.php
- License text source: Inno Setup License (as distributed with Inno Setup)

Copyright notices from Inno Setup license:

- Copyright (C) 1997-2026 Jordan Russell. All rights reserved.
- Portions Copyright (C) 2000-2026 Martijn Laan. All rights reserved.

Inno Setup license summary for this repository usage:

- This repository does not redistribute Inno Setup binaries.
- Inno Setup is used as a build-time tool via a local installation of `ISCC.exe`.
- Installer scripts in this repository target Inno Setup but are not modified Inno Setup source.

If Inno Setup source or binaries are redistributed in the future, ensure full compliance with all conditions in the Inno Setup License, including retention of required notices and clear marking of modified versions.

## LibGit2Sharp

This project uses LibGit2Sharp for managed Git repository access in the bridge tooling.

- Package: LibGit2Sharp
- Website: https://github.com/libgit2/libgit2sharp
- License: MIT

Repository usage summary:

- LibGit2Sharp is used to reduce reliance on command-line Git execution for read-only repository inspection flows.
- This repository should retain the LibGit2Sharp copyright and license notice when redistributing builds that include the package.
- If redistributed package contents include additional native dependency notices, those notices should be carried forward with the installer and release artifacts.
