# VS IDE Bridge Repo Instructions

## MCP Entry Point

- MCP server name: `vs-ide-bridge`
- Installed CLI: `C:\Program Files\VsIdeBridge\cli\vs-ide-bridge.exe`
- Start with: `mcp-server --tools-only`
- `vs-ide-bridge-pr` is a compatibility alias only — prefer `vs-ide-bridge` when available.

## Start of Session Checklist

1. `bind_solution` — bind to `VsIdeBridge.sln` (or use `solution_hint: "vs-ide-bridge"`)
2. `bridge_health` — confirm live instance and solution binding
3. `diagnostics_snapshot` — get current IDE state, errors, and warnings before starting any work
4. `tool_help` — get the schema and examples for any tool before using it

## Deferred Tool Discovery (Claude Code)

All bridge MCP tools are deferred in Claude Code. Before you can call any bridge tool,
you must fetch its schema with `ToolSearch`. Batch-fetch tools you expect to need:
`select:mcp__vs-ide-bridge__list_instances,mcp__vs-ide-bridge__bind_solution,mcp__vs-ide-bridge__read_file,mcp__vs-ide-bridge__apply_diff,mcp__vs-ide-bridge__errors,mcp__vs-ide-bridge__warnings,mcp__vs-ide-bridge__find_text,mcp__vs-ide-bridge__find_text_batch,mcp__vs-ide-bridge__build`

## Key Tools for This Repo

| Task | Tool |
|------|------|
| Take one code slice | `read_file` (reveals in VS editor) |
| Take several code slices | `read_file_batch` |
| Search code | `find_text`, `search_symbols` |
| Bulk search (many queries) | `find_text_batch` — one round-trip for multiple queries |
| Find files by name | `find_files` |
| Check errors/warnings | `diagnostics_snapshot` |
| Make code changes | `apply_diff` (applies live into VS editor) |
| Build | `build` or `build_errors` |
| Bump version | `set_version` |
| Build installer | `shell_exec exe:"powershell" args:"-ExecutionPolicy Bypass -File scripts\\build-setup.ps1"` |
| Build + installer | `shell_exec exe:"powershell" args:"-ExecutionPolicy Bypass -File scripts\\build-setup.ps1 -Build -Configuration Release"` |
| Open VS if closed | `vs_open` then `wait_for_instance` |

## Bulk Refactoring Workflow

When making many similar changes across a large file:
1. Use `find_text_batch` with all the target strings to get every location at once
2. Read the relevant sections with `read_file` (tight line ranges)
3. Apply changes with `apply_diff` — combine adjacent changes into single hunks to avoid overlap errors
4. Check `warnings` and `errors` after all edits to verify

## Important: apply_diff Path Resolution

`apply_diff` resolves paths relative to the solution directory. Changes land in the live VS editor buffer — they are not auto-saved.

## Code Slice Guidance

- When a human says "take a slice of the code," use `read_file` or `read_file_batch`, not `open_file` alone.
- Use `read_file` for one anchor or one range.
- Use `read_file_batch` when you need several slices and want one bridge round-trip.

## Source Of Truth

- The installed bridge stack under `C:\Program Files\VsIdeBridge\...` is the **runtime** source of truth.
- The repo source may be ahead of the installed version. If they disagree, say so explicitly.
- After building and installing a new version, MCP tools may disappear until the client session restarts.

## Local Config Files

- Codex global MCP config: `%USERPROFILE%\.codex\config.toml`
- Claude Code / JSON-based MCP config: `.mcp.json`
- Continue workspace MCP config: `.continue/mcpServers/vs-ide-bridge.yaml`
- Use the same installed command and args everywhere: `C:\Program Files\VsIdeBridge\cli\vs-ide-bridge.exe` with `mcp-server --tools-only`
- Only add `--instance <instanceId>` when you intentionally want to pin one client to one specific Visual Studio window

## Fallback

- If MCP is unavailable, fall back to the installed CLI (`C:\Program Files\VsIdeBridge\cli\vs-ide-bridge.exe`), not repo-local build outputs.
