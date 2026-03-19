using System.Text.Json.Nodes;

namespace VsIdeBridgeCli;

internal static partial class CliApp
{
    private static partial class McpServer
    {
        private static JsonNode ListToolsCompact()
            => Registry.Definitions.BuildCompactToolsList();

        private static JsonNode ListToolCategories()
            => Registry.Definitions.BuildCategoryList();

        private static JsonNode ListToolsByCategory(JsonObject? args)
        {
            string category = args?["category"]?.GetValue<string>() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(category))
                return new JsonObject { ["error"] = "category parameter is required" };

            return Registry.Definitions.BuildToolsByCategory(category);
        }

        private static JsonNode RecommendTools(JsonObject? args)
        {
            string task = args?["task"]?.GetValue<string>() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(task))
                return new JsonObject { ["error"] = "task parameter is required" };

            return Registry.Definitions.RecommendTools(task);
        }
    }
}
