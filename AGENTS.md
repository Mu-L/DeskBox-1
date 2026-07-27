# DeskBox development workflow

- After changing application code, first stop any running `DeskBox.exe` whose executable path is under this repository, then build the affected project, and start a fresh instance from the current Debug build unless the user explicitly asks not to restart it. Stopping before the build avoids locking the output executable.
- The canonical local development executable is `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`.
- After starting DeskBox, verify that exactly the intended repository build is running and report the executable path.
- Do not launch DeskBox from `Output`, `artifacts`, `.artifacts`, or `src/DeskBox/AppPackages` unless the user explicitly requests testing a packaged or published build.
- Preserve unrelated user changes and release artifacts. Ask before deleting material output directories or installer packages unless the user explicitly authorizes their removal.
