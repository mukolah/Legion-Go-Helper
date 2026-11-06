# Development notes

## Build and release
```powershell
cd app
dotnet publish -c Release -o ..\dist      # single-file, framework-dependent dist\LegionHelper.exe (~0.6 MB)
```
- Bump `<Version>` in `app/LegionHelper.csproj` and add a `CHANGELOG.md` entry for every release.
- Release builds contain no PDB and no local paths (`DebugType=none`, `PathMap`, `Deterministic`).
- Publish `dist\LegionHelper.exe` as a GitHub Release asset. Build output, `dist/` and the Lenovo installer are
  git-ignored and must never be committed.
- The app requires administrator rights (the Lenovo WMI methods need them). Settings and log are in
  `%AppData%\LegionHelper\` (`config.json`, `log.txt`; unhandled UI exceptions are logged there too).

## Before publishing to GitHub
1. `git init`, `git add .`, then check `git status`. Only source, docs, `LICENSE`, `README.md`, `CHANGELOG.md` and
   `.gitignore` should be listed.
2. Search the repo for usernames, paths and machine names. Docs and screenshots were checked to contain none.
3. Create the Release with the published exe.

## How things are tested
There is no automated test suite. Hardware behaviour was verified on a Legion Go S 8ARP1 like this:
- **Firmware (WMI):** `Get-CimInstance` / `Invoke-CimMethod` on `root\WMI` (`LENOVO_*` classes). Read → write →
  read back → restore. Allow about 1 s before reading back.
- **Controller HID:** a small throwaway console tool built against `app/Hardware/LegionController.cs` sent
  `[cmd, sub, data]` queries and printed replies. The same approach was used for the LampArray (`LampArray.cs`) and
  for recording all controller input reports (which is how Legion L/R were found in `mi_06`).
- **UI:** UI Automation to find buttons by name, plus window screenshots via `PrintWindow`.
  - The themed `RButton` ignores the UIA Invoke pattern. Click it with a real mouse click at the centre of its
    bounding rectangle. Filter by control type Button, because labels can share a name (e.g. "Controller").
  - The sub-windows (Fans, Controller, RGB, Extra, Shortcuts) are owned by the main window, so they always stay above
    it. Hide them before clicking the main window, and check `WindowFromPoint` before every click.
  - Find the sub-windows with `EnumWindows` filtered by process id; UIA doesn't always list them as top-level.
  - Hide windows the way a user does (the close button / `WM_SYSCOMMAND SC_CLOSE`). A plain `WM_CLOSE` is not
    `UserClosing`, so it quits the app.
- **Screenshots** in `docs/images/` are `PrintWindow` captures (flag `PW_RENDERFULLCONTENT`) cropped to the DWM
  frame bounds, taken at 175% scaling. Refresh them when the UI changes, and check that they show no personal
  names or paths.
- **Always restore the user's state** (mode, limits, fan table, lighting, mappings) after a hardware test.

## Where things are
See the "Source layout" section in `README.md`, plus `docs/hardware-interface.md` (firmware WMI),
`docs/controller-protocol.md` (controller HID, per-LED LampArray, state stream) and
`docs/reverse-engineering.md` (methodology).

## Ideas not yet done
- Gyro-as-joystick, and gyro activation while holding a button.
- Per-mode custom presets ("my Quiet" = custom limits plus a curve).
- Combination overlays on the RGB rings (e.g. mode colour as base with a battery warning on top). The low-battery
  overlay already works this way.
- Reading the back paddles from the `mi_06` state stream instead of remapping them to F23/F24. That could free the
  paddles for other uses; their bits haven't been mapped yet.
- Original Legion Go support (different controller, `SapientiaUsb`). Not covered.
