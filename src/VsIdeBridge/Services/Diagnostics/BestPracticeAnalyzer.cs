using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace VsIdeBridge.Services.Diagnostics;

internal static class BestPracticeAnalyzer
{
    // All Find* methods moved here, updated to use ErrorListConstants, ErrorListPatterns, BestPracticeRuleCatalog, DiagnosticRowFactory

    // Example:
    public static IEnumerable<JObject> FindRepeatedStringLiterals(string file, string content)
    {
        var matches = ErrorListPatterns.StringLiteralPattern.Matches(content);
        var stringCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match match in matches)
        {
            var str = match.Groups[1].Value;
            stringCounts[str] = (stringCounts.TryGetValue(str, out int count) ? count : 0) + 1;
        }

        var findingCount = 0;
        foreach (var kvp in stringCounts.Where(kvp => kvp.Value >= BestPracticeRuleCatalog.BP1001.Threshold))
        {
            yield return DiagnosticRowFactory.CreateBestPracticeRow(
                code: BestPracticeRuleCatalog.BP1001.Code,
                message: $"String \"{Truncate(kvp.Key, 40)}\" appears {kvp.Value} times. Consider extracting to a constant.",
                file: file,
                line: GetLineNumber(content, content.IndexOf($"\"{kvp.Key}\"", StringComparison.Ordinal)),
                symbol: kvp.Key,
                helpUri: BestPracticeRuleCatalog.BP1001.HelpUri);
            findingCount++;
            if (findingCount >= ErrorListConstants.MaxSuppressionFindingsPerFile) { yield break; }
        }
    }

    // Include all other Find* methods similarly updated.

    private static int GetLineNumber(string content, int index)
    {
        return content.Take(index).Count(c => c == '\n') + 1;
    }

    private static string Truncate(string text, int maxLength)
    {
        return text.Length <= maxLength ? text : text.Substring(0, maxLength) + "...";
    }

    // Helper methods like IsTrulyUsefulComment, etc., moved here.
}