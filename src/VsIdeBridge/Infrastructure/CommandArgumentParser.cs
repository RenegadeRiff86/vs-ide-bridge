using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace VsIdeBridge.Infrastructure;

internal static class CommandArgumentParser
{
    private const string InvalidArgumentsCode = "invalid_arguments";

    public static CommandArguments Parse(JToken? rawArguments)
    {
        return rawArguments switch
        {
            null => new CommandArguments([]),
            JValue { Type: JTokenType.Null } => new CommandArguments([]),
            JValue value => Parse(value.ToString()),
            JObject obj => ParseObject(obj),
            _ => throw new CommandErrorException(InvalidArgumentsCode, "Arguments must be a string or JSON object."),
        };
    }

    public static CommandArguments Parse(string? rawArguments)
    {
        List<string> tokens = Tokenize(rawArguments ?? string.Empty);
        Dictionary<string, List<string>> values = [];

        for (int i = 0; i < tokens.Count; i++)
        {
            string token = tokens[i];
            if (!token.StartsWith("--", System.StringComparison.Ordinal))
            {
                throw new CommandErrorException(InvalidArgumentsCode, $"Unexpected token '{token}'. Arguments must use --name value form.");
            }

            string name = token.Substring(2).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new CommandErrorException(InvalidArgumentsCode, "Encountered an empty argument name.");
            }

            string value;
            if (i + 1 < tokens.Count && !tokens[i + 1].StartsWith("--", System.StringComparison.Ordinal))
            {
                value = tokens[++i];
            }
            else
            {
                value = "true";
            }

            if (!values.TryGetValue(name, out List<string>? list))
            {
                list = [];
                values[name] = list;
            }

            list.Add(value);
        }

        return new CommandArguments(values);
    }

    private static CommandArguments ParseObject(JObject obj)
    {
        Dictionary<string, List<string>> values = [];
        foreach (JProperty property in obj.Properties())
        {
            string name = property.Name.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new CommandErrorException(InvalidArgumentsCode, "Encountered an empty argument name.");
            }

            List<string> parsedValues = ParsePropertyValues(property.Value);
            if (parsedValues.Count == 0)
            {
                continue;
            }

            values[name] = parsedValues;
        }

        return new CommandArguments(values);
    }

    private static List<string> ParsePropertyValues(JToken token)
    {
        return token switch
        {
            JArray arr => [.. arr.Children().SelectMany(ParsePropertyValues)],
            JValue { Type: JTokenType.Null } => [],
            JValue value => [value.ToString()],
            JObject => throw new CommandErrorException(InvalidArgumentsCode, "Nested JSON objects are not supported in command args."),
            _ => [token.ToString()],
        };
    }

    private static List<string> Tokenize(string text)
    {
        List<string> tokens = [];
        StringBuilder buffer = new();
        bool inQuotes = false;

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '\\' && i + 1 < text.Length && (text[i + 1] == '"' || text[i + 1] == '\\'))
            {
                buffer.Append(text[i + 1]);
                i++;
                continue;
            }
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(ch))
            {
                FlushToken(tokens, buffer);
                continue;
            }

            buffer.Append(ch);
        }

        if (inQuotes)
        {
            throw new CommandErrorException("invalid_arguments", "Unterminated quoted argument.");
        }

        FlushToken(tokens, buffer);
        return tokens;
    }

    private static void FlushToken(List<string> tokens, StringBuilder buffer)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        tokens.Add(buffer.ToString());
        buffer.Clear();
    }
}
