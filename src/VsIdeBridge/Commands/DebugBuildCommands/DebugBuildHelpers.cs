using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Services;

namespace VsIdeBridge.Commands;

internal static partial class DebugBuildCommands
{
    private static ErrorListQuery CreateErrorListQuery(CommandArguments args, string? defaultSeverity = null)
    {
        return new ErrorListQuery
        {
            Severity = args.GetString("severity") ?? defaultSeverity,
            Code = args.GetString("code"),
            Project = args.GetString("project"),
            Path = args.GetString("path"),
            Text = args.GetString("text"),
            GroupBy = args.GetString("group-by"),
            Max = args.GetNullableInt32("max"),
        };
    }

    private static bool GetDiagnosticsForceRefresh(CommandArguments args)
        => args.GetBoolean("refresh", false);

    private static JObject FilterRowsBySeverity(JArray allRows, string severity, int? max)
    {
        IEnumerable<JToken> filtered = allRows
            .Where(r => string.Equals((string?)r["severity"], severity, StringComparison.OrdinalIgnoreCase));
        if (max is > 0)
            filtered = filtered.Take(max.Value);

        JToken[] commandResult = [.. filtered];
        return new JObject
        {
            ["count"] = commandResult.Length,
            ["rows"] = new JArray(commandResult.Select(r => r.DeepClone())),
        };
    }

    private static Task<JObject> GetSeverityDiagnosticsAsync(
        IdeCommandContext context,
        bool waitForIntellisense,
        int timeoutMilliseconds,
        bool quickSnapshot,
        string severity,
        int? max)
    {
        return GetDiagnosticsWithFallbackAsync(
            context,
            waitForIntellisense,
            timeoutMilliseconds,
            quickSnapshot,
            new ErrorListQuery
            {
                Severity = severity,
                Max = max,
            });
    }
}
