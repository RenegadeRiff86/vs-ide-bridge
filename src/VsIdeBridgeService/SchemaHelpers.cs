using System.Text.Json.Nodes;

#nullable enable
namespace VsIdeBridgeService;

// JSON Schema builder helpers for MCP tool input schemas.
internal static class SchemaHelpers
{
    private const string DescriptionPropertyName = "description";

    public static JsonObject EmptySchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),
        ["additionalProperties"] = false,
    };

    public static JsonObject ObjectSchema(params (string Name, JsonObject Schema, bool Required)[] properties)
    {
        JsonObject schema = new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(),
            ["additionalProperties"] = false,
        };

        JsonObject bag = (JsonObject)schema["properties"]!;
        JsonArray required = [];

        foreach ((string name, JsonObject propSchema, bool isRequired) in properties)
        {
            bag[name] = propSchema;
            if (isRequired)
                required.Add(name);
        }

        if (required.Count > 0)
            schema["required"] = required;

        return schema;
    }

    public static JsonObject StringSchema(string description) => new()
    {
        ["type"] = "string",
        [DescriptionPropertyName] = description,
    };

    public static JsonObject IntegerSchema(string description) => new()
    {
        ["type"] = "integer",
        [DescriptionPropertyName] = description,
    };

    public static JsonObject BooleanSchema(string description) => new()
    {
        ["type"] = "boolean",
        [DescriptionPropertyName] = description,
    };

    public static JsonObject ArrayOfStringsSchema(string description) => new()
    {
        ["type"] = "array",
        [DescriptionPropertyName] = description,
        ["items"] = new JsonObject { ["type"] = "string" },
    };

    public static JsonObject ArrayOfObjectsSchema(string description, JsonObject itemSchema) => new()
    {
        ["type"] = "array",
        [DescriptionPropertyName] = description,
        ["items"] = itemSchema,
    };

    public static JsonObject ArraySchema(string description, string itemType) => new()
    {
        ["type"] = "array",
        [DescriptionPropertyName] = description,
        ["items"] = new JsonObject { ["type"] = itemType },
    };

    // Property tuple factories — pass directly to ObjectSchema.
    public static (string, JsonObject, bool) Req(string name, string description)
        => (name, StringSchema(description), true);

    public static (string, JsonObject, bool) ReqInt(string name, string description)
        => (name, IntegerSchema(description), true);

    public static (string, JsonObject, bool) Opt(string name, string description)
        => (name, StringSchema(description), false);

    public static (string, JsonObject, bool) OptInt(string name, string description)
        => (name, IntegerSchema(description), false);

    public static (string, JsonObject, bool) OptBool(string name, string description)
        => (name, BooleanSchema(description), false);

    public static (string, JsonObject, bool) OptArr(string name, string description)
        => (name, ArrayOfStringsSchema(description), false);

    public static (string, JsonObject, bool) ReqArr(string name, string description)
        => (name, ArrayOfStringsSchema(description), true);
}
