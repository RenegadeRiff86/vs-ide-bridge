# Changelog

## 2.2.11

- Switched the installer and installed runtime fully to the service-backed MCP layout. The release removes the stale `cli` payload, fixes uninstall metadata, and adds installer support for enabling the local HTTP MCP endpoint.
- Fixed bridge discovery and command dispatch issues that could hide tools or break batched requests. This includes better discovery defaults, `glob` visibility, typed `batch` step arguments, and safer `BridgeCommand` registration behavior.
- Improved diagnostics and build behavior for large solutions. Passive warning and message reads now avoid unnecessary UI refreshes by default, quick fallback paths are stronger, and long-running solution builds report background progress without burning tokens.
- Reduced UI-thread stalls during search and investigation. `find_text_batch` now captures the search snapshot once per batch, and watchdog telemetry now records the active command when a probe timeout is detected.
- Cleaned analyzer and message-level issues in the bridge repo instead of suppressing them, and updated MCP setup documentation for both Codex and Claude Code with the current installed service path and config shapes.
