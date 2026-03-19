using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace VsIdeBridge.Services.Diagnostics;

internal static class BuildOutputParser
{
    public static IReadOnlyList<JObject> ParseBuildOutput(string buildOutputText)
    {
        var diagnostics = new List<JObject>();

        var lines = buildOutputText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var diagnostic = TryParseMsBuildDiagnostic(line) ?? TryParseStructuredOutput(line);
            if (diagnostic != null)
            {
                diagnostics.Add(diagnostic);
            }
        }

        return diagnostics;
    }

    private static JObject? TryParseMsBuildDiagnostic(string line)
    {
        var match = ErrorListPatterns.MsBuildDiagnosticPattern.Match(line);
        if (!match.Success) return null;

        var file = match.Groups["file"].Value;
        var lineNum = int.TryParse(match.Groups["line"].Value, out var ln) ? ln : 1;
        var column = int.TryParse(match.Groups["column"].Value, out var col) ? col : 1;
        var severity = match.Groups["severity"].Value;
        var code = match.Groups["code"].Value;
        var message = match.Groups["message"].Value;
        var project = match.Groups["project"].Value;

        return new JObject
        {
            [ErrorListConstants.SeverityKey] = severity,
            [ErrorListConstants.CodeKey] = code,
            [ErrorListConstants.ProjectKey] = project,
            [ErrorListConstants.FileKey] = file,
            [ErrorListConstants.LineKey] = lineNum,
            [ErrorListConstants.ColumnKey] = column,
            [ErrorListConstants.MessageKey] = message,
            [ErrorListConstants.ToolKey] = "MSBuild",
            [ErrorListConstants.SourceKey] = "Build",
        };
    }

    private static JObject? TryParseStructuredOutput(string line)
    {
        var match = ErrorListPatterns.StructuredOutputPattern.Match(line);
        if (!match.Success) return null;

        var project = match.Groups["project"].Value;
        var file = match.Groups["file"].Value;
        var lineNum = int.TryParse(match.Groups["line"].Value, out var ln) ? ln : 1;
        var column = int.TryParse(match.Groups["column"].Value, out var col) ? col : 1;
        var severity = match.Groups["severity"].Value;
        var code = match.Groups["code"].Value;
        var message = match.Groups["message"].Value;

        return new JObject
        {
            [ErrorListConstants.SeverityKey] = severity,
            [ErrorListConstants.CodeKey] = code,
            [ErrorListConstants.ProjectKey] = project,
            [ErrorListConstants.FileKey] = file,
            [ErrorListConstants.LineKey] = lineNum,
            [ErrorListConstants.ColumnKey] = column,
            [ErrorListConstants.MessageKey] = message,
            [ErrorListConstants.ToolKey] = "MSBuild",
            [ErrorListConstants.SourceKey] = "Build",
        };
    }
}