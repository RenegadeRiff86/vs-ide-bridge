using System.Text.Json.Nodes;
using static VsIdeBridgeService.ArgBuilder;
using static VsIdeBridgeService.SchemaHelpers;
using VsIdeBridge.Shared;

namespace VsIdeBridgeService;

internal static partial class ToolCatalog
{
    private const string BindSolutionToolName = "bind_solution";
    private const string ListInstancesToolName = "list_instances";
    private const string VsStateToolName = "vs_state";
    private const string WaitForReadyToolName = "wait_for_ready";

    private static IEnumerable<ToolEntry> CoreTools() =>
        CoreBindingTools()
            .Concat(CoreRegistryTools())
            .Concat(CoreSystemTools());

    private static IEnumerable<ToolEntry> CoreBindingTools()
    {
        yield return new("bridge_health",
            "Get binding health, discovery source, and last round-trip metrics.",
            EmptySchema(), Core,
            (id, _, bridge) => BridgeHealthAsync(id, bridge),
            searchHints: BuildSearchHints(
                related: [(VsStateToolName, "Check current IDE state"), (ListInstancesToolName, "Find all bridge instances")]));

        yield return BridgeTool("batch",
            "Execute multiple bridge commands in one round-trip. Use when you need results from " +
            "several commands together (e.g. state + errors + list-projects). " +
            "Steps format: [{\"command\":\"state\"},{\"command\":\"errors\",\"args\":\"{\\\"max\\\":20}\"},{\"command\":\"list-projects\"}]. " +
            "Note: prefer read_file_batch for multiple file reads and find_text_batch for multiple searches.",
            ObjectSchema(Req("steps",
                "JSON array of command steps. Each step: {\"command\":\"cmd-name\",\"args\":\"{...}\",\"id\":\"optional-label\"}. " +
                "Example: [{\"command\":\"state\"},{\"command\":\"errors\",\"args\":\"{\\\"max\\\":20}\"}]")),
            "batch",
            a => Build(("steps", OptionalString(a, "steps"))),
            Core,
            searchHints: BuildSearchHints(
                related: [("vs_state", "Check IDE state"), ("errors", "Fetch diagnostics"), ("read_file_batch", "Batch-read multiple files instead")]));

        yield return new(ListInstancesToolName,
            "List live VS IDE Bridge instances visible to this MCP server.",
            EmptySchema(), Core,
            (_, _, bridge) => ListInstancesAsync(bridge),
            searchHints: BuildSearchHints(
                workflow: [(BindSolutionToolName, "Bind session to a VS instance by solution name"), ("bind_instance", "Bind to a specific instance by ID")],
                related: [("bridge_health", "Check current binding health")]));

        yield return new("bind_instance",
            "Bind this MCP session to one specific Visual Studio bridge instance by " +
            "instance id, process id, or pipe name.",
            ObjectSchema(
                Opt("instance_id", "Optional exact bridge instance id."),
                OptInt("pid", "Optional Visual Studio process id."),
                Opt("pipe_name", "Optional exact bridge pipe name."),
                Opt("solution_hint", "Optional solution path or name substring.")),
            Core,
            async (id, args, bridge) =>
                (JsonNode)ToolResultFormatter.StructuredToolResult(await bridge.BindAsync(id, args).ConfigureAwait(false)),
            searchHints: BuildSearchHints(
                workflow: [(VsStateToolName, "Confirm IDE state after binding"), (WaitForReadyToolName, "Wait for IntelliSense to load")],
                related: [(BindSolutionToolName, "Bind by solution name"), (ListInstancesToolName, "List available instances")]));

        yield return new(BindSolutionToolName,
            "Bind this MCP session to a VS instance whose solution matches a name or path hint.",
            ObjectSchema(Req("solution", "Solution name or path substring to match.")),
            Core,
            async (id, args, bridge) =>
            {
                JsonObject bindArgs = new() { ["solution_hint"] = args?["solution"]?.DeepClone() };
                return (JsonNode)ToolResultFormatter.StructuredToolResult(await bridge.BindAsync(id, bindArgs).ConfigureAwait(false));
            },
            searchHints: BuildSearchHints(
                workflow: [(VsStateToolName, "Confirm IDE state after binding"), (WaitForReadyToolName, "Wait for IntelliSense to load")],
                related: [("bind_instance", "Bind by instance ID"), (ListInstancesToolName, "List available instances")]));

        yield return BridgeTool(VsStateToolName,
            "Current VS editor state — active document, build mode, solution, and debugger.",
            EmptySchema(), "state", _ => Empty(),
            aliases: ["bridge_state", "get_vs_state", "ide_state"],
            summary: "Current VS editor state — active document, build mode, solution, and debugger.",
            searchHints: BuildSearchHints(
                workflow: [("errors", "Check diagnostics"), ("read_file", "Read the active document")],
                related: [("bridge_health", "Check binding health"), (WaitForReadyToolName, "Wait for IntelliSense")]));

        yield return BridgeTool(WaitForReadyToolName,
            "Block until Visual Studio and IntelliSense are fully loaded. Call this after " +
            "open_solution or vs_open before running any semantic tools. This is intentionally slower than normal inspection commands.",
            EmptySchema(), "ready", _ => Empty(),
            searchHints: BuildSearchHints(
                workflow: [("errors", "Check diagnostics after loading"), ("search_symbols", "Search for symbols"), ("file_outline", "Inspect file structure")],
                related: [(VsStateToolName, "Check current IDE state")]));
    }

    private static IEnumerable<ToolEntry> CoreRegistryTools()
    {
        yield return new(
            ToolDefinitionCatalog.ListTools(EmptySchema())
                .WithSearchHints(BuildSearchHints(
                    workflow: [("list_tools_by_category", "Get a focused list of tools for a specific category"), ("recommend_tools", "Get personalized tool recommendations for a task")],
                    related: [("tool_help", "Get detailed help for a specific tool")])),
            (_, _, _) =>
            {
                JsonObject toolsList = DefinitionRegistry.Value.BuildCompactToolsList();
                return Task.FromResult<JsonNode>(
                    ToolResultFormatter.StructuredToolResult(toolsList, successText: toolsList.ToJsonString()));
            });

        yield return new(
            ToolDefinitionCatalog.ListToolCategories(EmptySchema())
                .WithSearchHints(BuildSearchHints(
                    workflow: [("list_tools_by_category", "Get tools for a specific category")],
                    related: [("list_tools", "List all tools at once"), ("recommend_tools", "Get recommendations for a task")])),
            (_, _, _) =>
            {
                JsonObject categories = DefinitionRegistry.Value.BuildCategoryList();
                return Task.FromResult<JsonNode>(
                    ToolResultFormatter.StructuredToolResult(categories, successText: categories.ToJsonString()));
            });

        const string CategoryName = "category";
        yield return new(
            ToolDefinitionCatalog.ListToolsByCategory(
                ObjectSchema(Req(CategoryName, "Category name.")))
                .WithSearchHints(BuildSearchHints(
                    workflow: [("tool_help", "Get detailed help for a tool from this category")],
                    related: [("list_tools", "List all tools"), ("recommend_tools", "Get recommendations for a specific task")])),
            (_, args, _) =>
            {
                JsonObject toolsByCategory = DefinitionRegistry.Value.BuildToolsByCategory(
                    args?["category"]?.GetValue<string>() ?? string.Empty);
                return Task.FromResult<JsonNode>(
                    ToolResultFormatter.StructuredToolResult(toolsByCategory, successText: toolsByCategory.ToJsonString()));
            });

        yield return new(
            ToolDefinitionCatalog.RecommendTools(
                ObjectSchema(Req("task", "Natural-language description of what you want to do.")))
                .WithSearchHints(BuildSearchHints(
                    workflow: [("list_tools_by_category", "Browse tools in the suggested category"), ("tool_help", "Get detailed help for a recommended tool")],
                    related: [("list_tools", "See all available tools")])),
            (_, args, _) =>
            {
                JsonObject recommendations = DefinitionRegistry.Value.RecommendTools(
                    args?["task"]?.GetValue<string>() ?? string.Empty);
                return Task.FromResult<JsonNode>(
                    ToolResultFormatter.StructuredToolResult(recommendations, successText: recommendations.ToJsonString()));
            });

        yield return new(
            ToolDefinitionCatalog.ToolHelp(
                ObjectSchema(
                    Opt("name", "Optional tool name for focused help."),
                    Opt(CategoryName, "Optional category name.")))
                .WithSearchHints(BuildSearchHints(
                    related: [("list_tools_by_category", "Browse tools by category"), ("recommend_tools", "Get recommendations for a task")])),
            (_, args, _) =>
            {
                JsonObject help = DefinitionRegistry.Value.BuildToolHelp(
                    args?["name"]?.GetValue<string>(),
                    args?["category"]?.GetValue<string>());
                return Task.FromResult<JsonNode>(
                    ToolResultFormatter.StructuredToolResult(help, successText: help.ToJsonString()));
            });
    }

    private static IEnumerable<ToolEntry> CoreSystemTools()
    {
        // yield return new("vs_open",
        //     "Launch a new Visual Studio instance. This launch path is disabled by default until you explicitly enable it for testing. Prefer starting Visual Studio manually and binding to it for normal use.",
        //     ObjectSchema(
        //         Opt("solution", "Absolute path to a .sln or .slnx file to open."),
        //         Opt("devenv_path", "Explicit path to devenv.exe. Auto-detected if omitted.")),
        //     SystemCategory,
        //     (id, args, bridge) => VsOpenAsync(id, args, bridge),
        //     searchHints: BuildSearchHints(
        //         workflow: [("wait_for_instance", "Wait for the instance to appear"), ("bind_solution", "Bind to the launched solution")],
        //         related: [("vs_close", "Close a VS instance"), ("vs_open_enable", "Enable bridge-driven launch first")]));

        yield return new("vs_open_enable",
            "Enable bridge-driven Visual Studio launch for deliberate testing. This persists until disabled.",
            EmptySchema(), Core,
            (_, _, _) => Task.FromResult<JsonNode>(ToolResultFormatter.StructuredToolResult(VsOpenLaunchController.Enable())),
            searchHints: BuildSearchHints(
                workflow: [("vs_open", "Launch a VS instance after enabling")],
                related: [("vs_open_disable", "Disable bridge-driven launch"), ("vs_open_status", "Check current status")]));

        yield return new("vs_open_disable",
            "Disable bridge-driven Visual Studio launch and return to the safer manual-start workflow.",
            EmptySchema(), Core,
            (_, _, _) => Task.FromResult<JsonNode>(ToolResultFormatter.StructuredToolResult(VsOpenLaunchController.Disable())),
            searchHints: BuildSearchHints(
                related: [("vs_open_enable", "Re-enable launch"), ("vs_open_status", "Check current status")]));

        yield return new("vs_open_status",
            "Show whether bridge-driven Visual Studio launch is currently enabled for testing.",
            EmptySchema(), Core,
            (_, _, _) => Task.FromResult<JsonNode>(ToolResultFormatter.StructuredToolResult(VsOpenLaunchController.GetStatus())),
            searchHints: BuildSearchHints(
                related: [("vs_open_enable", "Enable bridge-driven launch"), ("vs_open_disable", "Disable bridge-driven launch")]));

        yield return new("wait_for_instance",
            "Wait for a newly launched Visual Studio bridge instance to appear and become ready. Prefer list_instances polling over this tool.",
            ObjectSchema(
                Opt("solution", "Optional absolute path to the .sln or .slnx file you expect."),
                OptInt("timeout_ms", "How long to wait in milliseconds (default 60000).")),
            SystemCategory,
            (id, args, bridge) => WaitForInstanceAsync(id, args, bridge),
            searchHints: BuildSearchHints(
                workflow: [("bind_solution", "Bind to the appeared instance"), ("wait_for_ready", "Wait for IntelliSense to load")],
                related: [("list_instances", "List all visible instances")]));

        yield return BridgeTool("ui_settings",
            "Read current IDE Bridge UI and security settings.",
            EmptySchema(), "ui-settings", _ => Empty(),
            searchHints: BuildSearchHints(
                related: [("vs_state", "Check IDE state"), ("bridge_health", "Check binding health")]));

        yield return BridgeTool("capture_vs_window",
            "Activate the bound Visual Studio main window, bring it to the foreground, and capture only that window to a PNG image.",
            ObjectSchema(
                Opt("out", "Optional output PNG path. If omitted, saves under %TEMP%\\vs-ide-bridge\\screenshots.")),
            "capture-vs-window",
            a => Build(("out", OptionalString(a, "out"))),
            category: "documents",
            summary: "Activate the bound Visual Studio window and capture only that window to a PNG.",
            readOnly: true,
            mutating: false,
            destructive: false,
            searchHints: BuildSearchHints(
                workflow: [("vs_state", "Confirm the bound instance before capture")],
                related: [("activate_window", "Bring a specific tool window forward first"), ("list_windows", "Inspect current VS windows")]));

        yield return new("http_enable",
            "Start the HTTP MCP server on localhost:8080. " +
            "Enables Ollama and other local LLM clients to connect directly to the bridge. " +
            "The enabled state persists across restarts. " +
            "Clients send POST requests with JSON-RPC 2.0 bodies to http://localhost:8080/.",
            EmptySchema(), Core,
            (_, _, _) => Task.FromResult<JsonNode>(ToolResultFormatter.StructuredToolResult(HttpServerController.Enable())),
            searchHints: BuildSearchHints(
                workflow: [("http_status", "Verify the server is running")],
                related: [("http_disable", "Stop the HTTP server"), ("http_status", "Check server status")]));

        yield return new("http_disable",
            "Stop the HTTP MCP server and persist the disabled state across restarts.",
            EmptySchema(), Core,
            (_, _, _) => Task.FromResult<JsonNode>(ToolResultFormatter.StructuredToolResult(HttpServerController.Disable())),
            searchHints: BuildSearchHints(
                related: [("http_enable", "Start the HTTP server"), ("http_status", "Check server status")]));

        yield return new("http_status",
            "Show whether the HTTP MCP server is running, its port, and the URL to connect to.",
            EmptySchema(), Core,
            (_, _, _) => Task.FromResult<JsonNode>(ToolResultFormatter.StructuredToolResult(HttpServerController.GetStatus())),
            searchHints: BuildSearchHints(
                related: [("http_enable", "Start the HTTP server"), ("http_disable", "Stop the HTTP server")]));
    }
}
