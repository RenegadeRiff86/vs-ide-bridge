using System.Text.Json.Nodes;

namespace VsIdeBridge.Shared;

public sealed class ToolDefinition
{
    private const string ReadOnlyHintPropertyName = "readOnlyHint";
    private const string MutatingHintPropertyName = "mutatingHint";
    private const string DestructiveHintPropertyName = "destructiveHint";
    private const string TitlePropertyName = "title";
    private const string AnnotationsPropertyName = "annotations";
    private const string InputSchemaPropertyName = "inputSchema";
    private const string OutputSchemaPropertyName = "outputSchema";
    private const string DescriptionPropertyName = "description";
    private const string CategoryPropertyName = "category";
    private const string SummaryPropertyName = "summary";
    private const string AliasesPropertyName = "aliases";
    private const string TagsPropertyName = "tags";
    private const string BridgeCommandPropertyName = "bridgeCommand";

    public ToolDefinition(
        string name,
        string category,
        string summary,
        string description,
        JsonObject parameterSchema,
        bool readOnly = false,
        bool mutating = false,
        bool destructive = false,
        IEnumerable<string>? aliases = null,
        IEnumerable<string>? tags = null,
        string? bridgeCommand = null,
        string? title = null,
        JsonObject? annotations = null,
        JsonObject? outputSchema = null)
    {
        Name = name;
        Category = category;
        Summary = summary;
        Description = description;
        ParameterSchema = parameterSchema;
        ReadOnly = readOnly;
        Mutating = destructive || mutating;
        Destructive = destructive;
        Aliases = aliases?.Where(static alias => !string.IsNullOrWhiteSpace(alias))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? [];
        Tags = tags?.Where(static tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? [];
        BridgeCommand = bridgeCommand;
        Title = title;
        AdditionalAnnotations = annotations;
        OutputSchema = outputSchema;
    }

    public string Name { get; }

    public string Category { get; }

    public string Summary { get; }

    public string Description { get; }

    public JsonObject ParameterSchema { get; }

    public bool ReadOnly { get; }

    public bool Mutating { get; }

    public bool Destructive { get; }

    public IReadOnlyList<string> Aliases { get; }

    public IReadOnlyList<string> Tags { get; }

    public string? BridgeCommand { get; }

    public string? Title { get; }

    public JsonObject? AdditionalAnnotations { get; }

    public JsonObject? OutputSchema { get; }

    public static ToolDefinition CreateLegacy(
        string name,
        string category,
        string description,
        JsonObject parameterSchema,
        string? title = null,
        JsonObject? annotations = null,
        JsonObject? outputSchema = null,
        IEnumerable<string>? aliases = null,
        IEnumerable<string>? tags = null,
        string? bridgeCommand = null,
        string? summary = null,
        bool? readOnly = null,
        bool? mutating = null,
        bool? destructive = null)
    {
        bool resolvedReadOnly = readOnly ?? GetAnnotationBoolean(annotations, ReadOnlyHintPropertyName)
            ?? InferReadOnly(name);
        bool resolvedDestructive = destructive ?? GetAnnotationBoolean(annotations, DestructiveHintPropertyName)
            ?? InferDestructive(name);
        bool resolvedMutating = mutating ?? GetAnnotationBoolean(annotations, MutatingHintPropertyName)
            ?? InferMutating(name, resolvedReadOnly, resolvedDestructive);

        if (resolvedReadOnly)
        {
            resolvedMutating = false;
            resolvedDestructive = false;
        }

        return new ToolDefinition(
            name,
            category,
            summary ?? DeriveSummary(description),
            description,
            parameterSchema,
            resolvedReadOnly,
            resolvedMutating,
            resolvedDestructive,
            aliases,
            tags,
            bridgeCommand,
            title,
            annotations,
            outputSchema);
    }

    public JsonObject BuildCompactDiscoveryEntry()
    {
        JsonObject entry = new JsonObject
        {
            ["name"] = Name,
            [CategoryPropertyName] = Category,
            [SummaryPropertyName] = Summary,
            ["readOnly"] = ReadOnly,
            ["mutating"] = Mutating,
            ["destructive"] = Destructive,
        };

        if (Aliases.Count > 0)
            entry[AliasesPropertyName] = ToJsonArray(Aliases);

        if (Tags.Count > 0)
            entry[TagsPropertyName] = ToJsonArray(Tags);

        return entry;
    }

    public JsonObject BuildToolObject()
    {
        JsonObject tool = new JsonObject
        {
            ["name"] = Name,
            [DescriptionPropertyName] = Description,
            [InputSchemaPropertyName] = ParameterSchema.DeepClone(),
        };

        if (!string.IsNullOrWhiteSpace(Title))
            tool[TitlePropertyName] = Title;

        JsonObject annotations = BuildAnnotations();
        if (annotations.Count > 0)
            tool[AnnotationsPropertyName] = annotations;

        if (OutputSchema is not null)
            tool[OutputSchemaPropertyName] = OutputSchema.DeepClone();

        return tool;
    }

    public JsonObject BuildHelpEntry(string example, string? bridgeExample = null)
    {
        JsonObject entry = BuildCompactDiscoveryEntry();
        entry[DescriptionPropertyName] = Description;
        entry[InputSchemaPropertyName] = ParameterSchema.DeepClone();
        entry["example"] = example;

        if (!string.IsNullOrWhiteSpace(Title))
            entry[TitlePropertyName] = Title;

        if (!string.IsNullOrWhiteSpace(BridgeCommand))
            entry[BridgeCommandPropertyName] = BridgeCommand;

        if (!string.IsNullOrWhiteSpace(bridgeExample))
            entry["bridgeExample"] = bridgeExample;

        JsonObject annotations = BuildAnnotations();
        if (annotations.Count > 0)
            entry[AnnotationsPropertyName] = annotations;

        if (OutputSchema is not null)
            entry[OutputSchemaPropertyName] = OutputSchema.DeepClone();

        return entry;
    }

    public JsonObject BuildAnnotations()
    {
        JsonObject annotations = AdditionalAnnotations?.DeepClone().AsObject() ?? new JsonObject();

        annotations[ReadOnlyHintPropertyName] = ReadOnly;
        annotations[MutatingHintPropertyName] = Mutating;
        annotations[DestructiveHintPropertyName] = Destructive;
        annotations[CategoryPropertyName] = Category;
        annotations[SummaryPropertyName] = Summary;

        if (Aliases.Count > 0)
            annotations[AliasesPropertyName] = ToJsonArray(Aliases);

        if (Tags.Count > 0)
            annotations[TagsPropertyName] = ToJsonArray(Tags);

        if (!string.IsNullOrWhiteSpace(BridgeCommand))
            annotations[BridgeCommandPropertyName] = BridgeCommand;

        return annotations;
    }

    private static string DeriveSummary(string description)
    {
        string sentence = description.Trim();
        int sentenceBoundary = FindSentenceBoundary(sentence);
        if (sentenceBoundary >= 0)
            sentence = sentence[..sentenceBoundary].Trim();

        if (sentence.Length <= 96)
            return sentence;

        return sentence[..93].TrimEnd() + "...";
    }

    private static int FindSentenceBoundary(string description)
    {
        for (int index = 0; index < description.Length; index++)
        {
            if (description[index] != '.')
                continue;

            if (index == description.Length - 1)
                return index;

            if (char.IsWhiteSpace(description[index + 1]))
                return index;
        }

        return -1;
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        JsonArray array = new JsonArray();
        foreach (string value in values)
            array.Add(value);

        return array;
    }

    private static bool? GetAnnotationBoolean(JsonObject? annotations, string propertyName)
    {
        return annotations?[propertyName]?.GetValue<bool>();
    }

    private static bool InferReadOnly(string name)
    {
        return name.StartsWith("list_", StringComparison.Ordinal)
            || name.StartsWith("find_", StringComparison.Ordinal)
            || name.StartsWith("read_", StringComparison.Ordinal)
            || name.StartsWith("search_", StringComparison.Ordinal)
            || name.StartsWith("query_", StringComparison.Ordinal)
            || name.StartsWith("count_", StringComparison.Ordinal)
            || name.StartsWith("peek_", StringComparison.Ordinal)
            || name.StartsWith("goto_", StringComparison.Ordinal)
            || name.StartsWith("python_list_", StringComparison.Ordinal)
            || name.StartsWith("python_get_", StringComparison.Ordinal)
            || name is "bridge_health"
                or "bridge_state"
                or "wait_for_ready"
                or "ui_settings"
                or "tool_help"
                or "help"
                or "errors"
                or "warnings"
                or "build_configurations"
                or "diagnostics_snapshot"
                or "debug_stack"
                or "debug_locals"
                or "debug_threads"
                or "debug_modules"
                or "debug_watch"
                or "debug_exceptions"
                or "symbol_info"
                or "file_outline"
                or "search_symbols"
                or "read_file"
                or "read_file_batch"
                or "find_text"
                or "find_text_batch"
                or "find_references"
                or "peek_definition"
                or "list_tools"
                or "list_tool_categories"
                or "list_tools_by_category"
                or "recommend_tools"
                or "python_env_info"
                or "git_status"
                or "git_diff_staged"
                or "git_diff_unstaged"
                or "git_log"
                or "git_current_branch"
                or "git_branch_list"
                or "git_remote_list"
                or "git_show"
                or "git_stash_list"
                or "git_tag_list"
                or "github_issue_search";
    }

    private static bool InferDestructive(string name)
    {
        return name is "apply_diff"
            or "write_file"
            or "set_version"
            or "clear_breakpoints"
            or "github_issue_close"
            or "vs_close"
            || name.StartsWith("git_", StringComparison.Ordinal)
            || name.StartsWith("nuget_", StringComparison.Ordinal)
            || name.StartsWith("conda_", StringComparison.Ordinal)
            || name.StartsWith("python_install_", StringComparison.Ordinal)
            || name.StartsWith("python_remove_", StringComparison.Ordinal)
            || name.StartsWith("python_create_", StringComparison.Ordinal);
    }

    private static bool InferMutating(string name, bool readOnly, bool destructive)
    {
        if (readOnly)
            return false;

        if (destructive)
            return true;

        return name.StartsWith("bind_", StringComparison.Ordinal)
            || name.StartsWith("open_", StringComparison.Ordinal)
            || name.StartsWith("close_", StringComparison.Ordinal)
            || name.StartsWith("save_", StringComparison.Ordinal)
            || name.StartsWith("activate_", StringComparison.Ordinal)
            || name.StartsWith("reload_", StringComparison.Ordinal)
            || name.StartsWith("format_", StringComparison.Ordinal)
            || name.StartsWith("set_", StringComparison.Ordinal)
            || name.StartsWith("create_", StringComparison.Ordinal)
            || name.StartsWith("add_", StringComparison.Ordinal)
            || name.StartsWith("remove_", StringComparison.Ordinal)
            || name.StartsWith("write_", StringComparison.Ordinal)
            || name.StartsWith("apply_", StringComparison.Ordinal)
            || name is "build"
                or "build_errors"
                or "vs_open"
                or "wait_for_instance"
                or "execute_command"
                or "shell_exec";
    }
}
