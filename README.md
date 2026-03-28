# VS IDE Bridge

VS IDE Bridge lets an LLM work against your actual live Visual Studio instance in real time so it can read code, navigate symbols, apply diffs, inspect diagnostics, build projects, and run service-native helper tools.

This file is the main product and operator guide.

- For LLM-specific workflow rules, read [AGENTS.md](AGENTS.md).
- For current known problems, read [BUGS.md](BUGS.md).
- For architecture ownership, read [ARCHITECTURE_HIERARCHY.md](ARCHITECTURE_HIERARCHY.md).

## What It Is

The product has two main runtime pieces:

- `VsIdeBridge` is the Visual Studio extension. It runs inside Visual Studio and exposes IDE operations over the bridge pipe.
- `VsIdeBridgeService` is the Windows service. It is the always-ready MCP-facing runtime and also hosts service-native tools.

There are two broad tool families:

- Bridge tools talk to a live Visual Studio instance through the named pipe.
- Service-native tools run inside the Windows service and do not require Visual Studio to be open.

## Current Runtime Truth

The source and the installed product do not currently support the same client-hosting story in every path. The following statements are the verified behavior as of March 28, 2026:

- The Windows service `VsIdeBridgeService` is the primary installed runtime.
- Running `C:\Program Files\VsIdeBridge\service\VsIdeBridgeService.exe mcp-server` starts a separate foreground MCP host process.
- That `mcp-server` mode does not attach to the already-running SCM-managed Windows service.
- The optional HTTP MCP listener is the only verified in-process reuse path for the existing Windows service.
- The installed `C:\Program Files\VsIdeBridge\cli\vs-ide-bridge.exe` is not currently a verified stdio attach client.
- The installed bridge exposes stateless Python scratchpad tools (`python_eval` and `python_exec`) for math and quick transforms.
- The installed bridge does not currently expose a persistent Python REPL session tool.

If you need the latest caveats, check [BUGS.md](BUGS.md) before configuring a client.

## Installed Layout

Typical installed paths:

- Service binary: `C:\Program Files\VsIdeBridge\service\VsIdeBridgeService.exe`
- Installed CLI path: `C:\Program Files\VsIdeBridge\cli\vs-ide-bridge.exe`
- VSIX payload: `C:\Program Files\VsIdeBridge\vsix\VsIdeBridge.vsix`
- Optional managed Python runtime: `C:\Program Files\VsIdeBridge\python\managed-runtime\python.exe`

## Recommended Usage

For local operation:

1. Install the product.
2. Verify the Windows service is running.
3. Open Visual Studio normally.
4. Let the extension register a bridge instance.
5. Bind your MCP client to that live instance.

For clients that can use HTTP MCP and you want to reuse the installed Windows service:

1. Enable the HTTP listener.
2. Connect the client to `http://localhost:8080/`.

For clients that require stdio MCP:

- `VsIdeBridgeService.exe mcp-server` is a dedicated foreground host mode, not service attachment.
- Use it only when that process model is acceptable.

## MCP Config Examples

These examples are intentionally explicit because the installed product supports two different MCP connection models today.

### HTTP MCP example

Use this when your client can talk to an MCP server over HTTP and you want to reuse the installed Windows service.

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

Operator notes:

- Enable the HTTP listener before using this config.
- This is the only verified in-process reuse path for the installed Windows service.

### Stdio MCP example

Use this only when your client requires stdio MCP and you are comfortable with a separate foreground host process.

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

Operator notes:

- This starts a dedicated foreground MCP host.
- It does not attach to the already-running SCM-managed Windows service.
- Do not describe this path as service attachment in docs or client examples.

### Tool safety example

If your MCP client supports allowlists, blocklists, or per-tool approvals, disable `shell_exec` by default and only enable it for trusted sessions.

Example shape:

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

The exact field name varies by client. Some use `disabledTools`, others use tool allowlists, approval rules, or policy settings. The important part is the policy: do not leave `shell_exec` broadly available unless you intend to give the model arbitrary command execution.

## Shell Safety

`shell_exec` is intentionally a last-resort tool. If you allow an LLM to use it freely, you are effectively allowing it to run arbitrary shell commands on the machine with access equivalent to the bridge host process.

- Do not expose `shell_exec` to untrusted prompts or unattended automation unless you are comfortable with that level of control.
- Prefer the bridge's typed tools for editing, diagnostics, build, Git, Python scratchpad work, and project inspection before allowing shell access.
- Treat `shell_exec` as an operator-approved escape hatch, not a normal path for day-to-day use.

## Visual Studio Flow

The normal product flow is:

1. Visual Studio starts.
2. The `VsIdeBridge` extension starts its pipe server inside Visual Studio.
3. The extension writes discovery metadata for the live IDE instance.
4. `VsIdeBridgeService` discovers that instance and routes bridge-tool calls to it.
5. MCP clients talk to either a foreground MCP host process or the optional HTTP listener, depending on how they are configured.

## Code Map

Important projects:

- `src/VsIdeBridge` - Visual Studio extension and pipe server
- `src/VsIdeBridgeService` - Windows service, MCP host wiring, service-native tools
- `src/Shared` - shared contracts, tool definitions, registry helpers
- `src/VsIdeBridgeInstaller` - installer packaging

Important docs:

- [AGENTS.md](AGENTS.md) - LLM workflow rules
- [BUGS.md](BUGS.md) - current issues and workarounds
- [ROADMAP.md](ROADMAP.md) - structural cleanup roadmap
- [SERVICE_CONVERSION_TRACKER.md](SERVICE_CONVERSION_TRACKER.md) - archived conversion history

## Tooling Notes

- Use the installed bridge tool catalog as the runtime source of truth.
- Use `tool_help`, `list_tool_categories`, and `list_tools` to inspect the live installed surface.
- Prefer bridge-native edits for files in the active Visual Studio solution.
- Use `python_eval` for one-expression math checks and `python_exec` for short stateless snippets.
- Do not document Python support as a REPL until a persistent session tool actually exists.
- Do not normalize `shell_exec` as a convenience tool. Prefer typed bridge tools first, because `shell_exec` can run arbitrary commands when the host allows it.

## Status

The project is usable, but the docs must be read with the current runtime caveats in mind.

The two most important ones are:

- stdio `mcp-server` is a separate host process, not Windows service attachment
- the HTTP listener is the only confirmed service-reuse path today
