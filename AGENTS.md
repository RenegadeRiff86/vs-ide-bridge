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

## Key Tools for This Repo

| Task | Tool |
|------|------|
| Read source code | `read_file` (reveals in VS editor) |
| Search code | `find_text`, `search_symbols` |
| Check errors/warnings | `diagnostics_snapshot` |
| Make code changes | `apply_diff` (applies live into VS editor) |
| Build | `build` or `build_errors` |
| Bump version | `set_version` |
| Build installer | `shell_exec` with `scripts\build-setup.ps1` |
| Open VS if closed | `vs_open` then `wait_for_instance` |

## Important: apply_diff Path Resolution

`apply_diff` resolves paths relative to the solution directory. Changes land in the live VS editor buffer — they are not auto-saved.

## Source Of Truth

- The installed bridge stack under `C:\Program Files\VsIdeBridge\...` is the **runtime** source of truth.
- The repo source may be ahead of the installed version. If they disagree, say so explicitly.
- After building and installing a new version, MCP tools may disappear until the client session restarts.

## Local Config Files

- Project-local MCP config: `.mcp.json`
- Continue workspace MCP config: `.continue/mcpServers/vs-ide-bridge.yaml`

## Fallback

- If MCP is unavailable, fall back to the installed CLI (`C:\Program Files\VsIdeBridge\cli\vs-ide-bridge.exe`), not repo-local build outputs.
