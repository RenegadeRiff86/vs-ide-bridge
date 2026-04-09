# VS IDE Bridge

VS IDE Bridge is an MCP server that connects AI assistants to a live Visual Studio instance. It gives an LLM real-time access to your code, diagnostics, symbols, editor, debugger, Git history, and project structure — all through the IDE that is already open on your machine.

## How It Works

Two components run side by side:

- **VsIdeBridge** — a Visual Studio extension that runs inside VS, owns the named pipe server, and exposes IDE operations.
- **VsIdeBridgeService** — a Windows service that is the MCP-facing runtime. It routes bridge-tool calls through the pipe to whichever VS instance is active, and hosts service-native tools that do not require VS to be open.

When Visual Studio starts, the extension registers a live bridge instance. The service discovers it and makes it available to any connected MCP client.

```
MCP Client  ──►  VsIdeBridgeService  ──►  [named pipe]  ──►  VsIdeBridge (inside VS)
                  (HTTP or stdio MCP)                          (extension, live IDE)
```

## Requirements

- Windows 10 or Windows 11
- Visual Studio 2022 (17.x) or Visual Studio 2025 (18.x)
- A compatible MCP client (Claude Desktop, Cursor, or any client that speaks HTTP MCP or stdio MCP)

## Installation

1. Download the latest release installer.
2. Run the installer. It installs the Windows service, the VS extension, and the CLI.
3. Start Visual Studio. The extension auto-registers the bridge instance on startup.
4. Connect your MCP client using one of the config examples below.

After installation, the default layout is:

```
C:\Program Files\VsIdeBridge\
  service\VsIdeBridgeService.exe   ← MCP host and Windows service
  cli\vs-ide-bridge.exe            ← CLI entry point
  vsix\VsIdeBridge.vsix            ← VS extension payload
  python\managed-runtime\          ← optional managed Python runtime
```

## MCP Client Setup

The bridge supports two connection models.

### HTTP MCP (recommended)

Use this when your client can speak HTTP MCP. It reuses the already-running Windows service.

Enable the HTTP listener once:

In Visual Studio: **Tools → IdeVSBridgeMenu → Enable HTTP MCP**

Or from the CLI:

```
vs-ide-bridge http enable
```

Then add to your MCP client config:

```json
{
  "mcpServers": {
    "vs-ide-bridge": {
      "transport": {
        "type": "http",
        "url": "http://localhost:8080/"
      }
    }
  }
}
```

### Stdio MCP

Use this when your client requires stdio (Claude Code, Cursor, and most IDE-native clients). It starts a dedicated foreground host process — this is not attachment to the already-running Windows service.

#### Claude Code

Run once to register the server globally:

```bash
claude mcp add vs-ide-bridge "C:\Program Files\VsIdeBridge\service\VsIdeBridgeService.exe" mcp-server
```

Or add it manually to `~/.claude.json` under `mcpServers`:

```json
{
  "mcpServers": {
    "vs-ide-bridge": {
      "command": "C:\\Program Files\\VsIdeBridge\\service\\VsIdeBridgeService.exe",
      "args": ["mcp-server"]
    }
  }
}
```

#### Other stdio clients

Use the same `command` / `args` pattern in your client's MCP config file.

### Tool safety

If your client supports per-tool controls, disable `shell_exec` by default:

```json
{
  "mcpServers": {
    "vs-ide-bridge": {
      "command": "C:\\Program Files\\VsIdeBridge\\service\\VsIdeBridgeService.exe",
      "args": ["mcp-server"],
      "disabledTools": ["shell_exec"]
    }
  }
}
```

See [Shell Safety](#shell-safety) below.

## Quick Start

Once connected, the bridge auto-binds to a live VS instance when it sees one clear target. Explicit binding is better when you have multiple VS windows open:

```
list_instances        ← see what VS instances are available
bind_instance         ← bind the session to one specific instance
wait_for_ready        ← wait for IntelliSense to finish loading
```

From there you can read code, navigate symbols, apply edits, inspect diagnostics, and run builds without leaving your AI chat.

## Tool Overview

The bridge currently exposes **147 tools** across 9 categories. Use `list_tool_categories`, `list_tools`, and `list_tools_by_category` for live discovery, and `recommend_tools` to ask the bridge which tools fit a given task.

### core — Session and binding

Connect to the right VS instance and manage the current session.

`list_instances` · `bind_instance` · `bind_solution` · `bridge_health` · `vs_state` · `wait_for_ready` · `open_file` · `save_document` · `reload_document` · `activate_document` · `activate_window` · `list_windows` · `http_enable` · `http_status`

### search — Code navigation

Find files, inspect symbols, read code, and trace definitions and references.

`find_files` · `find_text` · `find_text_batch` · `search_symbols` · `read_file` · `read_file_batch` · `file_outline` · `file_symbols` · `symbol_info` · `peek_definition` · `goto_definition` · `goto_implementation` · `find_references` · `count_references` · `call_hierarchy` · `smart_context`

### diagnostics — Errors and build

Inspect the live Error List and drive builds.

`errors` · `warnings` · `messages` · `diagnostics_snapshot` · `build` · `build_solution` · `rebuild_solution` · `build_errors` · `build_configurations` · `set_build_configuration` · `run_code_analysis`

### documents — Editor and files

Patch code through the live editor and manage open documents.

`apply_diff` · `capture_vs_window` · `write_file` · `list_documents` · `list_tabs` · `copy_file` · `delete_file` · `format_document`

`apply_diff` uses the bridge editor-patch format:

```
*** Begin Patch
*** Update File: src/MyProject/MyClass.cs
@@
-        string result = oldValue;
+        string result = newValue;
*** End Patch
```

### debug — Debugger inspection

Inspect and control an active debug session.

`debug_threads` · `debug_stack` · `debug_locals` · `debug_watch` · `debug_modules` · `debug_exceptions` · `set_breakpoint` · `list_breakpoints` · `enable_breakpoint` · `disable_breakpoint` · `debug_start` · `debug_break` · `debug_continue` · `debug_stop` · `debug_step_into` · `debug_step_over` · `debug_step_out`

### git — Version control

Bridge-managed Git operations with structured results.

`git_status` · `git_diff_unstaged` · `git_diff_staged` · `git_add` · `git_commit` · `git_push` · `git_pull` · `git_branch_list` · `git_checkout` · `git_log` · `git_show` · `git_stash_push` · `git_stash_pop`

### project — Projects and solutions

Inspect and modify project structure, references, NuGet packages, and outputs.

`list_projects` · `query_project_items` · `query_project_properties` · `query_project_references` · `query_project_outputs` · `query_project_configurations` · `scan_project_dependencies` · `add_file_to_project` · `remove_file_from_project` · `nuget_add_package` · `nuget_remove_package` · `set_startup_project`

### python — Python runtime

Interpreter discovery, package management, and stateless scratchpad execution.

`python_eval` · `python_exec` · `python_list_envs` · `python_env_info` · `python_list_packages` · `python_install_package` · `python_remove_package` · `python_create_env`

Note: this is a stateless scratchpad surface, not a persistent REPL.

### system — Discovery and host

Tool-catalog discovery and host-level control.

`list_tool_categories` · `list_tools` · `list_tools_by_category` · `recommend_tools` · `tool_help` · `wait_for_instance` · `vs_close` · `shell_exec` · `set_version`

`capture_vs_window` activates the bound Visual Studio window, brings it to the foreground, and writes a PNG to `%TEMP%\vs-ide-bridge\screenshots` by default so one-off captures do not dirty the repo.

## Shell Safety

`shell_exec` can run arbitrary commands on the host machine with the permissions of the bridge process. Treat it as an operator-approved escape hatch:

- Prefer typed bridge tools for editing, diagnostics, builds, Git, and project work before reaching for `shell_exec`.
- Do not expose `shell_exec` to untrusted prompts or unattended automation without review.
- If your MCP client supports tool allowlists or approval rules, add `shell_exec` to the requires-approval list.

## Best Practice Analyzer

VS IDE Bridge ships a built-in best-practice analyzer that runs over the solution and surfaces structural guidance alongside normal diagnostics — things like oversized files, long methods, accessor-heavy types, and commented-out code. The warnings appear in the Visual Studio Error List alongside normal compiler output and can be queried with `warnings`.

## Project Structure

```
src/
  VsIdeBridge/              ← VS extension: pipe server, commands, IDE services
  VsIdeBridgeService/       ← Windows service: MCP runtime, tool catalog, service tools
  Shared/                   ← tool metadata, schemas, shared registry
  VsIdeBridge.Diagnostics/  ← best-practice rules and warning projection
  VsIdeBridgeInstaller/     ← installer entry point
  VsIdeBridgeLauncher/      ← VS launch helper
installer/                  ← Inno Setup packaging
tests/                      ← unit and integration tests
codex/                      ← SDK notes and workflow skills
```

## Building from Source

Prerequisites: Visual Studio 2022 or later with the Visual Studio extension development workload.

```
git clone https://github.com/<owner>/vs-ide-bridge
cd vs-ide-bridge
dotnet build
```

Open `VsIdeBridge.sln` in Visual Studio to work on the extension. The VSIX project sets up the experimental instance automatically for F5 debugging.

## Third-Party Notices

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for license information covering third-party components used in this project.
