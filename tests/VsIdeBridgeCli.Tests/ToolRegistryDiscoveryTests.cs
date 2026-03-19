using System.Text.Json.Nodes;
using VsIdeBridge.Shared;
using Xunit;

namespace VsIdeBridgeCli.Tests;

public sealed class ToolRegistryDiscoveryTests
{
    [Fact]
    public void BuildCategoryList_IncludesRequiredCategories()
    {
        ToolRegistry registry = CreateRegistry();

        JsonObject result = registry.BuildCategoryList();
        JsonArray categories = result["categories"]!.AsArray();
        string[] names = categories
            .Select(static item => item!["name"]!.GetValue<string>())
            .ToArray();

        Assert.Contains("core", names);
        Assert.Contains("search", names);
        Assert.Contains("diagnostics", names);
        Assert.Contains("documents", names);
        Assert.Contains("debug", names);
        Assert.Contains("project", names);
        Assert.Contains("system", names);
    }

    [Fact]
    public void BuildCompactToolsList_SurfacesCompactSafetyMetadata()
    {
        ToolRegistry registry = CreateRegistry();

        JsonObject result = registry.BuildCompactToolsList();
        JsonArray tools = result["tools"]!.AsArray();
        JsonObject firstTool = tools[0]!.AsObject();

        Assert.True(result["navigationToolsFirst"]!.GetValue<bool>());
        Assert.True(firstTool.ContainsKey("name"));
        Assert.True(firstTool.ContainsKey("category"));
        Assert.True(firstTool.ContainsKey("summary"));
        Assert.True(firstTool.ContainsKey("readOnly"));
        Assert.True(firstTool.ContainsKey("mutating"));
        Assert.True(firstTool.ContainsKey("destructive"));
        Assert.Equal("find_text", firstTool["name"]!.GetValue<string>());
    }

    [Fact]
    public void BuildToolsByCategory_SearchIncludesNavigationTools()
    {
        ToolRegistry registry = CreateRegistry();

        JsonObject result = registry.BuildToolsByCategory("search");
        JsonArray tools = result["tools"]!.AsArray();
        string[] names = tools
            .Select(static item => item!["name"]!.GetValue<string>())
            .ToArray();

        Assert.Contains("search_symbols", names);
        Assert.Contains("read_file", names);
        Assert.Contains("find_text", names);
        Assert.Contains("find_references", names);
        Assert.Contains("peek_definition", names);
        Assert.Contains("file_outline", names);
    }

    [Fact]
    public void RecommendTools_PrioritizesCodeNavigationTools()
    {
        ToolRegistry registry = CreateRegistry();

        JsonObject result = registry.RecommendTools("find the definition and references for this symbol");
        JsonArray recommendations = result["recommendations"]!.AsArray();
        string[] names = recommendations
            .Select(static item => item!["name"]!.GetValue<string>())
            .ToArray();

        Assert.Contains("search_symbols", names);
        Assert.Contains("peek_definition", names);
        Assert.Contains("find_references", names);
        Assert.Contains("read_file", names);
    }

    [Fact]
    public void RecommendTools_PrioritizesBridgeEditTools()
    {
        ToolRegistry registry = CreateRegistry();

        JsonObject result = registry.RecommendTools("patch this file to fix the error and update the method");
        JsonArray recommendations = result["recommendations"]!.AsArray();
        string[] names = recommendations
            .Select(static item => item!["name"]!.GetValue<string>())
            .ToArray();

        Assert.Contains("apply_diff", names);
        Assert.Contains("write_file", names);
    }

    private static ToolRegistry CreateRegistry()
    {
        JsonObject emptySchema = CreateEmptySchema();
        JsonObject categorySchema = CreateSchemaWithRequiredString("category", "Category name.");
        JsonObject taskSchema = CreateSchemaWithRequiredString("task", "Task description.");
        ToolDefinition[] tools =
        [
            ToolDefinitionCatalog.ListTools(CreateEmptySchema()),
            ToolDefinitionCatalog.ListToolCategories(CreateEmptySchema()),
            ToolDefinitionCatalog.ListToolsByCategory(categorySchema),
            ToolDefinitionCatalog.RecommendTools(taskSchema),
            ToolDefinitionCatalog.ReadFile(emptySchema.DeepClone().AsObject()),
            ToolDefinitionCatalog.ReadFileBatch(emptySchema.DeepClone().AsObject()),
            ToolDefinitionCatalog.FindText(emptySchema.DeepClone().AsObject()),
            ToolDefinitionCatalog.FindTextBatch(emptySchema.DeepClone().AsObject()),
            ToolDefinitionCatalog.SearchSymbols(emptySchema.DeepClone().AsObject()),
            ToolDefinitionCatalog.Errors(emptySchema.DeepClone().AsObject()),
            ToolDefinitionCatalog.ApplyDiff(emptySchema.DeepClone().AsObject()),
            ToolDefinitionCatalog.WriteFile(emptySchema.DeepClone().AsObject()),
            ToolDefinitionCatalog.FileOutline(emptySchema.DeepClone().AsObject()),
            ToolDefinitionCatalog.SymbolInfo(emptySchema.DeepClone().AsObject()),
            ToolDefinitionCatalog.PeekDefinition(emptySchema.DeepClone().AsObject()),
            ToolDefinitionCatalog.FindReferences(emptySchema.DeepClone().AsObject()),
        ];

        return new ToolRegistry(tools);
    }

    private static JsonObject CreateEmptySchema()
    {
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(),
        };
    }

    private static JsonObject CreateSchemaWithRequiredString(string name, string description)
    {
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                [name] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = description,
                },
            },
            ["required"] = new JsonArray(name),
        };
    }
}
