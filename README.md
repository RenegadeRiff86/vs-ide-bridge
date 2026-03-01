# VS IDE Bridge

Local Visual Studio 2026 extension for scriptable IDE control through stable `Tools.*` commands.

## Scope

This repo exposes Visual Studio automation commands that are easy to call from:

- the Visual Studio Command Window
- DTE automation
- wrapper scripts and agent tooling

The first release includes:

- IDE state snapshots
- solution readiness waiting
- file and text search with JSON output
- document/window activation
- breakpoint management
- debugger control and state capture
- build and Error List capture

It does not edit source text.

## Requirements

- Visual Studio 2026 / 18 Community, Pro, or Enterprise
- Windows

## Build

```bat
scripts\build_vsix.bat
```

## Install

```bat
scripts\install_vsix.bat
```

Installing the VSIX updates the extension in Visual Studio and may close `devenv.exe` if it is running.

## Core Commands

- `Tools.IdeGetState`
- `Tools.IdeWaitForReady`
- `Tools.IdeFindFiles`
- `Tools.IdeFindText`
- `Tools.IdeOpenDocument`
- `Tools.IdeActivateWindow`
- `Tools.IdeSetBreakpoint`
- `Tools.IdeListBreakpoints`
- `Tools.IdeRemoveBreakpoint`
- `Tools.IdeClearAllBreakpoints`
- `Tools.IdeDebugGetState`
- `Tools.IdeDebugStart`
- `Tools.IdeDebugStop`
- `Tools.IdeDebugBreak`
- `Tools.IdeDebugContinue`
- `Tools.IdeDebugStepOver`
- `Tools.IdeDebugStepInto`
- `Tools.IdeDebugStepOut`
- `Tools.IdeBuildSolution`
- `Tools.IdeGetErrorList`
- `Tools.IdeBuildAndCaptureErrors`

All operational commands accept an argument string and write a JSON result file.

Example:

```text
Tools.IdeGetState --out "C:\temp\ide-state.json"
```

Every command writes:

- a JSON envelope to the requested `--out` path
- a one-line summary to the `IDE Bridge` Output pane
- a status-bar summary

If `--out` is omitted, the extension writes to `%TEMP%\vs-ide-bridge\*.json`.

## Argument Contract

- `--out "C:\path\result.json"`: preferred output path
- `--request-id "abc123"`: optional correlation id
- `--timeout-ms 120000`: optional on wait/build commands
- booleans use `true` or `false`
- enum values use lowercase kebab-case

Examples:

```text
Tools.IdeWaitForReady --out "C:\temp\ready.json" --timeout-ms 120000
Tools.IdeFindFiles --query "VsIdeBridge.csproj" --out "C:\temp\files.json"
Tools.IdeFindText --query "Tools.IdeGetState" --scope solution --out "C:\temp\find.json"
Tools.IdeOpenDocument --file "C:\repo\src\foo.cpp" --line 42 --column 1 --out "C:\temp\open.json"
Tools.IdeSetBreakpoint --file "C:\repo\src\foo.cpp" --line 42 --out "C:\temp\bp.json"
Tools.IdeBuildAndCaptureErrors --out "C:\temp\build-errors.json" --timeout-ms 600000
```

## Scripts

- `scripts\invoke_vs_ide_command.ps1`
  Launch or attach to Visual Studio and invoke an IDE Bridge command.
- `scripts\smoke_test.ps1`
  Run a small end-to-end validation against the extension.
- `scripts\vs_dte_probe.ps1`
  Inspect live Visual Studio 18 DTE instances.

## Wrapper Usage

Use the PowerShell wrapper when you want to drive the extension from outside Visual Studio:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\invoke_vs_ide_command.ps1 `
  -SolutionPath C:\Users\elsto\source\repos\vs-ide-bridge\VsIdeBridge.sln `
  -CommandName Tools.IdeGetState `
  -OutputPath C:\temp\ide-state.json
```

Key wrapper behavior:

- reuses an existing Visual Studio 18 instance when the requested solution is already open
- can open the requested solution in a blank VS instance
- leaves Visual Studio open by default
- only closes Visual Studio when `-CloseVisualStudio` is passed
- waits for the output JSON file to be written before returning

Run the smoke test with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\smoke_test.ps1
```

## Notes

- Search results are written to JSON and surfaced in the `IDE Bridge` Output pane.
- Build and Error List commands reuse the same Error List extraction logic that worked in `vs-errorlist-export`.
- The extension is command-first. The only visible menu items are `Help` and `Smoke Test`.
