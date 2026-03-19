using System.Text.Json.Nodes;

namespace VsIdeBridgeService;

// JSON Schema builder helpers for MCP tool input schemas.
internal static class SchemaHelpers
{
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
        JsonArray required = new();

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
        ["description"] = description,
    };

    public static JsonObject IntegerSchema(string description) => new()
    {
        ["type"] = "integer",
        ["description"] = description,
    };

    public static JsonObject BooleanSchema(string description) => new()
    {
        ["type"] = "boolean",
        ["description"] = description,
    };

    public static JsonObject ArrayOfStringsSchema(string description) => new()
    {
        ["type"] = "array",
        ["description"] = description,
        ["items"] = new JsonObject { ["type"] = "string" },
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
}
