using System;

namespace VsIdeBridgeService;

// Builds the canonical tool execution registry for the Windows service MCP server.
// This service is the primary MCP host; CLI hosting is backup only.
// Bridge commands route through the VS named pipe. Service-hosted tools stay local when
// there is no real bridge command behind the MCP surface.
internal static partial class ToolCatalog
{
    private static readonly Lazy<ToolExecutionRegistry> SharedRegistry = new(() => new ToolExecutionRegistry(CreateEntries()));

    public static ToolExecutionRegistry Registry => SharedRegistry.Value;

    public static ToolExecutionRegistry CreateRegistry()
    {
        return Registry;
    }

    private static IReadOnlyList<ToolEntry> CreateEntries()
    {
        ToolEntry[] entries =
        [
            // ── core: discovery + binding ──────────────────────────────────────
            .. CoreTools(),
            // ── search + read ──────────────────────────────────────────────────
            .. SearchTools(),
            // ── diagnostics + build ────────────────────────────────────────────
            .. DiagnosticsTools(),
            // ── semantic navigation ────────────────────────────────────────────
            .. SemanticTools(),
            // ── editor / document management ───────────────────────────────────
            .. DocumentTools(),
            // ── debug ──────────────────────────────────────────────────────────
            .. DebugTools(),
            // ── project management ─────────────────────────────────────────────
            .. ProjectTools(),
            // ── git (service-native subprocess) ───────────────────────────────
            .. GitTools(),
            // ── python env/package management (service-native subprocess) ──────
            .. PythonNativeTools(),
            // ── nuget package management (service-native subprocess) ───────────
            .. NugetTools(),
        ];

        return
        [
            .. entries.Select(EnrichSearchHints),
        ];
    }
}
