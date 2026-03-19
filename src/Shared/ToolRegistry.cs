using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;

namespace VsIdeBridge.Shared;

public sealed class ToolRegistry
{
    private static readonly string[] DefaultRecommendedNavigationTools =
    [
        "find_files",
        "search_symbols",
        "read_file",
        "peek_definition",
        "find_text",
        "find_references",
        "errors",
    ];

    private static readonly string[] DefaultRecommendedEditTools =
    [
        "apply_diff",
        "write_file",
    ];

    private static readonly string[] DefaultRecommendedBuildTools =
    [
        "build",
        "build_errors",
        "errors",
        "build_configurations",
        "set_build_configuration",
    ];

    private readonly ToolDefinition[] _all;
    private readonly Dictionary<string, ToolDefinition> _byNameOrAlias;
    private readonly ToolCategoryDefinition[] _categories;
    private readonly string[] _featuredTools;

    public ToolRegistry(
        IEnumerable<ToolDefinition> tools,
        IEnumerable<ToolCategoryDefinition>? categories = null,
        IEnumerable<string>? featuredTools = null)
    {
        _all = tools.OrderBy(static tool => tool.Name, StringComparer.Ordinal).ToArray();
        _categories = (categories ?? DefaultCategories)
            .OrderBy(static category => category.Name, StringComparer.Ordinal)
            .ToArray();
        _featuredTools = featuredTools?.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            ?? DefaultFeaturedTools.ToArray();
        _byNameOrAlias = BuildLookup(_all);
    }

    public static IReadOnlyList<ToolCategoryDefinition> DefaultCategories { get; } =
    [
        new("core", "Session and binding", "Connection, binding, state, and always-load basics."),
        new("search", "Code navigation", "Find text, read code, inspect symbols, and trace references."),
        new("diagnostics", "Errors and build", "Errors, warnings, build output, and diagnostics snapshots."),
        new("documents", "Editor and files", "Open, close, save, format, and manage files and windows."),
        new("debug", "Debugger inspection", "Breakpoints, stacks, locals, watches, threads, and modules."),
        new("git", "Version control", "Bridge-managed Git and GitHub helpers, with fallback paths where needed."),
        new("python", "Python runtime", "Python environments, packages, REPL, and run-file tools."),
        new("project", "Projects and solutions", "Projects, references, outputs, NuGet, and solution structure."),
        new("system", "Discovery and host", "Tool discovery, host control, and last-resort process execution."),
    ];

    public static IReadOnlyList<string> DefaultFeaturedTools { get; } =
    [
        "find_files",
        "find_text",
        "find_text_batch",
        "search_symbols",
        "read_file",
        "read_file_batch",
        "file_outline",
        "symbol_info",
        "peek_definition",
        "find_references",
        "errors",
        "apply_diff",
        "write_file",
    ];

    public IReadOnlyList<ToolDefinition> All => _all;

    public IReadOnlyList<ToolCategoryDefinition> Categories => _categories;

    public bool TryGet(string nameOrAlias, [NotNullWhen(true)] out ToolDefinition? tool)
        => _byNameOrAlias.TryGetValue(nameOrAlias, out tool);

    public IReadOnlyList<ToolDefinition> GetByCategory(string category)
    {
        return _all
            .Where(tool => string.Equals(tool.Category, category, StringComparison.OrdinalIgnoreCase))
            .OrderBy(static tool => tool.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public JsonObject BuildCategoryList()
    {
        JsonArray categories = new JsonArray();
        foreach (ToolCategoryDefinition category in _categories)
        {
            int count = _all.Count(tool => string.Equals(tool.Category, category.Name, StringComparison.Ordinal));
            categories.Add(new JsonObject
            {
                ["name"] = category.Name,
                ["summary"] = category.Summary,
                ["description"] = category.Description,
                ["toolCount"] = count,
            });
        }

        JsonArray featuredTools = new JsonArray();
        foreach (string toolName in _featuredTools)
            featuredTools.Add(toolName);

        return new JsonObject
        {
            ["count"] = categories.Count,
            ["categories"] = categories,
            ["featuredTools"] = featuredTools,
        };
    }

    public JsonObject BuildCompactToolsList()
    {
        JsonArray tools = new JsonArray();
        HashSet<string> emitted = new HashSet<string>(StringComparer.Ordinal);

        foreach (string featuredTool in _featuredTools)
        {
            if (TryGet(featuredTool, out ToolDefinition? tool) && emitted.Add(tool.Name))
                tools.Add(tool.BuildCompactDiscoveryEntry());
        }

        foreach (ToolDefinition tool in _all)
        {
            if (emitted.Add(tool.Name))
                tools.Add(tool.BuildCompactDiscoveryEntry());
        }

        return new JsonObject
        {
            ["navigationToolsFirst"] = true,
            ["count"] = tools.Count,
            ["tools"] = tools,
        };
    }

    public JsonObject BuildToolsByCategory(string category)
    {
        ToolCategoryDefinition? categoryDefinition = _categories.FirstOrDefault(
            item => string.Equals(item.Name, category, StringComparison.OrdinalIgnoreCase));
        if (categoryDefinition is null)
        {
            return new JsonObject
            {
                ["error"] = $"Unknown category '{category}'.",
                ["validCategories"] = new JsonArray(_categories.Select(item => JsonValue.Create(item.Name)).ToArray()),
            };
        }

        JsonArray tools = new JsonArray();
        foreach (ToolDefinition tool in GetByCategory(category))
            tools.Add(tool.BuildCompactDiscoveryEntry());

        return new JsonObject
        {
            ["category"] = categoryDefinition.Name,
            ["summary"] = categoryDefinition.Summary,
            ["description"] = categoryDefinition.Description,
            ["count"] = tools.Count,
            ["tools"] = tools,
        };
    }

    public JsonObject RecommendTools(string task)
    {
        JsonArray recommendations = new JsonArray();
        foreach ((ToolDefinition Tool, string Reason, int Score) recommendation in ScoreTools(task).Take(7))
        {
            recommendations.Add(new JsonObject
            {
                ["name"] = recommendation.Tool.Name,
                ["reason"] = recommendation.Reason,
                ["category"] = recommendation.Tool.Category,
                ["summary"] = recommendation.Tool.Summary,
            });
        }

        return new JsonObject
        {
            ["task"] = task,
            ["count"] = recommendations.Count,
            ["recommendations"] = recommendations,
        };
    }

    private IEnumerable<(ToolDefinition Tool, string Reason, int Score)> ScoreTools(string task)
    {
        string normalizedTask = task.Trim().ToLowerInvariant();
        string[] tokens = normalizedTask.Split(
            [' ', '_', '-', '.', ',', ':', ';', '?', '!', '/', '\\'],
            StringSplitOptions.RemoveEmptyEntries);
        bool looksLikeNavigationTask = ContainsAny(normalizedTask,
            "find", "search", "symbol", "definition", "reference", "read", "where",
            "inspect", "navigate", "understand", "outline", "trace", "code");
        bool looksLikeSolutionExplorerTask = ContainsAny(normalizedTask,
            "solution explorer", "filename", "file name", "path fragment", "path", "folder");
        bool looksLikeDiagnosticTask = ContainsAny(normalizedTask,
            "error", "warning", "diagnostic", "build", "broken", "failing");
        bool looksLikeEditTask = ContainsAny(normalizedTask,
            "change", "edit", "write", "patch", "refactor", "rename", "update",
            "fix", "create", "replace", "overwrite");
        bool looksLikeBuildTask = ContainsAny(normalizedTask,
            "build", "compile", "installer", "package", "publish", "msbuild", "rebuild");
        bool looksLikeExternalProcessTask = ContainsAny(normalizedTask,
            "powershell", "cmd", "command line", "process", "exe", "script", "iscc", "terminal");

        List<(ToolDefinition Tool, string Reason, int Score)> scored = [];
        foreach (ToolDefinition tool in _all)
        {
            int score = 0;
            string reason = string.Empty;

            if (looksLikeNavigationTask && _featuredTools.Contains(tool.Name, StringComparer.OrdinalIgnoreCase))
            {
                score += 40;
                reason = "Featured code navigation tool";
            }

            if (looksLikeNavigationTask && DefaultRecommendedNavigationTools.Contains(tool.Name, StringComparer.OrdinalIgnoreCase))
            {
                score += 45;
                reason = "Top navigation tool for code understanding";
            }

            if (looksLikeSolutionExplorerTask && string.Equals(tool.Name, "find_files", StringComparison.OrdinalIgnoreCase))
            {
                score += 70;
                reason = ChooseReason(reason, "Matches Solution Explorer-style file search task");
            }

            if (looksLikeNavigationTask && string.Equals(tool.Category, "search", StringComparison.OrdinalIgnoreCase))
            {
                score += 18;
                reason = ChooseReason(reason, "Search category matches code-navigation task");
            }

            if (looksLikeDiagnosticTask && string.Equals(tool.Category, "diagnostics", StringComparison.OrdinalIgnoreCase))
            {
                score += 22;
                reason = ChooseReason(reason, "Diagnostics category matches error/build task");
            }

            if (looksLikeBuildTask && DefaultRecommendedBuildTools.Contains(tool.Name, StringComparer.OrdinalIgnoreCase))
            {
                score += 45;
                reason = ChooseReason(reason, "Primary Visual Studio build tool");
            }

            if (looksLikeBuildTask && string.Equals(tool.Category, "diagnostics", StringComparison.OrdinalIgnoreCase))
            {
                score += 18;
                reason = ChooseReason(reason, "Diagnostics category matches build task");
            }

            if (looksLikeEditTask && DefaultRecommendedEditTools.Contains(tool.Name, StringComparer.OrdinalIgnoreCase))
            {
                score += 45;
                reason = ChooseReason(reason, "Primary bridge editing tool");
            }

            if (looksLikeEditTask && string.Equals(tool.Category, "documents", StringComparison.OrdinalIgnoreCase))
            {
                score += 20;
                reason = ChooseReason(reason, "Documents category matches file-edit task");
            }

            if (looksLikeEditTask && tool.Mutating)
            {
                score += 10;
                reason = ChooseReason(reason, "Mutating tool matches requested change");
            }

            if (tool.Tags.Count > 0)
            {
                int tagHits = tool.Tags.Count(tag => tokens.Contains(tag, StringComparer.OrdinalIgnoreCase));
                if (tagHits > 0)
                {
                    score += tagHits * 12;
                    reason = ChooseReason(reason, "Tool tags match task keywords");
                }
            }

            int nameHits = tokens.Count(token => token.Length > 2
                && tool.Name.Contains(token, StringComparison.OrdinalIgnoreCase));
            if (nameHits > 0)
            {
                score += nameHits * 8;
                reason = ChooseReason(reason, "Tool name matches task keywords");
            }

            int aliasHits = tokens.Count(token => token.Length > 2
                && tool.Aliases.Any(alias => alias.Contains(token, StringComparison.OrdinalIgnoreCase)));
            if (aliasHits > 0)
            {
                score += aliasHits * 10;
                reason = ChooseReason(reason, "Tool aliases match common IDE wording");
            }

            int summaryHits = tokens.Count(token => token.Length > 3
                && tool.Summary.Contains(token, StringComparison.OrdinalIgnoreCase));
            if (summaryHits > 0)
            {
                score += summaryHits * 4;
                reason = ChooseReason(reason, "Summary matches task context");
            }

            int descriptionHits = tokens.Count(token => token.Length > 3
                && tool.Description.Contains(token, StringComparison.OrdinalIgnoreCase));
            if (descriptionHits > 0)
            {
                score += descriptionHits * 3;
                reason = ChooseReason(reason, "Description matches task context");
            }

            if (string.Equals(tool.Name, "shell_exec", StringComparison.OrdinalIgnoreCase))
            {
                if (looksLikeExternalProcessTask)
                {
                    score -= 10;
                    reason = ChooseReason(reason, "External process task may require shell execution");
                }
                else
                {
                    score -= 80;
                }

                if (looksLikeBuildTask)
                    score -= 40;
            }

            if (score > 0)
                scored.Add((tool, reason == string.Empty ? "Broadly relevant tool" : reason, score));
        }

        return scored
            .OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.Tool.Name, StringComparer.Ordinal);
    }

    private static string ChooseReason(string current, string candidate)
        => string.IsNullOrWhiteSpace(current) ? candidate : current;

    private static bool ContainsAny(string text, params string[] values)
    {
        foreach (string value in values)
        {
            if (text.Contains(value, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static Dictionary<string, ToolDefinition> BuildLookup(IEnumerable<ToolDefinition> tools)
    {
        Dictionary<string, ToolDefinition> lookup = new Dictionary<string, ToolDefinition>(StringComparer.Ordinal);
        foreach (ToolDefinition tool in tools)
        {
            AddLookupEntry(lookup, tool.Name, tool);
            foreach (string alias in tool.Aliases)
                AddLookupEntry(lookup, alias, tool);
        }

        return lookup;
    }

    private static void AddLookupEntry(Dictionary<string, ToolDefinition> lookup, string key, ToolDefinition tool)
    {
        if (lookup.TryGetValue(key, out ToolDefinition? existing))
        {
            throw new InvalidOperationException(
                $"Duplicate tool lookup key '{key}' for '{existing.Name}' and '{tool.Name}'.");
        }

        lookup[key] = tool;
    }
}
