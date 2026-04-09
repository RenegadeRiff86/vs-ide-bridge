using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using static VsIdeBridge.Diagnostics.ErrorListPatterns;

namespace VsIdeBridge.Diagnostics;

internal static partial class BestPracticeAnalyzerHelpers
{
    internal static int CountBracedBlockLines(string[] lines, int startIndex)
    {
        int depth = 0;
        bool foundOpen = false;
        for (int i = startIndex; i < lines.Length; i++)
        {
            (depth, foundOpen) = ScanLineForBraces(lines[i], depth, foundOpen);
            if (foundOpen && depth <= 0)
            {
                return i - startIndex + 1;
            }
        }

        return lines.Length - startIndex;
    }

    internal static bool HasNearbyOpeningBrace(string[] lines, int startLine, int lookAheadLines)
    {
        int maxLineExclusive = Math.Min(lines.Length, startLine + lookAheadLines);
        for (int i = startLine - 1; i < maxLineExclusive; i++)
        {
            if (lines[i].Contains('{'))
            {
                return true;
            }
        }

        return false;
    }

    internal static int CountPythonFunctionLines(string[] lines, int startIndex)
    {
        if (startIndex >= lines.Length)
        {
            return 0;
        }

        string defLine = lines[startIndex];
        int baseIndent = defLine.Length - defLine.TrimStart().Length;
        int count = 1;
        for (int i = startIndex + 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                count++;
                continue;
            }

            int indent = line.Length - line.TrimStart().Length;
            if (indent <= baseIndent)
            {
                break;
            }

            count++;
        }

        return count;
    }

    internal static bool IsGeneratedRegexDeclaration(string[] lines, int startLine)
    {
        int lineIndex = startLine - 1;
        if (lineIndex < 0 || lineIndex >= lines.Length)
        {
            return false;
        }

        string declarationLine = lines[lineIndex];
        if (declarationLine.IndexOf("partial Regex", StringComparison.Ordinal) < 0)
        {
            return false;
        }

        for (int i = lineIndex - 1; i >= 0; i--)
        {
            string candidateLine = lines[i].Trim();
            if (string.IsNullOrEmpty(candidateLine))
            {
                continue;
            }

            return candidateLine.IndexOf("GeneratedRegex(", StringComparison.Ordinal) >= 0;
        }

        return false;
    }

    internal static string ExtractBracedBlock(string[] lines, int startIndex)
    {
        int depth = 0;
        bool foundOpen = false;
        List<string> blockLines = [];
        for (int i = startIndex; i < lines.Length; i++)
        {
            blockLines.Add(lines[i]);
            (depth, foundOpen) = ScanLineForBraces(lines[i], depth, foundOpen);
            if (foundOpen && depth <= 0)
            {
                break;
            }
        }

        return string.Join("\n", blockLines);
    }

    internal static string[] GetRelativeDirectorySegments(string directoryPath)
    {
        string[] pathSegments = directoryPath
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        int srcIndex = Array.FindLastIndex(pathSegments, static segment =>
            string.Equals(segment, "src", StringComparison.OrdinalIgnoreCase));
        if (srcIndex < 0 || srcIndex >= pathSegments.Length - 1)
        {
            return [];
        }

        int relativeLength = pathSegments.Length - srcIndex - 1;
        string[] relativeSegments = new string[relativeLength];
        Array.Copy(pathSegments, srcIndex + 1, relativeSegments, 0, relativeLength);
        return relativeSegments;
    }

    internal static string[] GetDeclaredPartialTypeNames(string content)
    {
        List<string> partialTypeNames = [];
        foreach (Match match in PartialTypeDeclarationPattern().Matches(content))
        {
            string typeName = match.Groups["name"].Value.TrimStart('@');
            if (!string.IsNullOrWhiteSpace(typeName))
            {
                partialTypeNames.Add(typeName);
            }
        }

        return [.. partialTypeNames.Distinct(StringComparer.Ordinal)];
    }

    internal static string[] TrimTypeGroupSegments(string[] directorySegments, string[] partialTypeNames)
    {
        if (partialTypeNames.Length == 0)
        {
            return directorySegments;
        }

        for (int index = directorySegments.Length - 1; index >= 0; index--)
        {
            if (partialTypeNames.Contains(directorySegments[index], StringComparer.Ordinal))
            {
                string[] trimmedSegments = new string[index];
                Array.Copy(directorySegments, 0, trimmedSegments, 0, index);
                return trimmedSegments;
            }
        }

        return directorySegments;
    }

    internal static bool NamespaceMatchesFolderStructure(string[] directorySegments, string[] namespaceSegments)
    {
        if (directorySegments.Length == 0)
        {
            return true;
        }

        string[] expandedDir = [..directorySegments.SelectMany(static seg => seg.Split('.'))];
        if (expandedDir.Length > namespaceSegments.Length)
        {
            return false;
        }

        int namespaceOffset = namespaceSegments.Length - expandedDir.Length;
        for (int index = 0; index < expandedDir.Length; index++)
        {
            if (!string.Equals(expandedDir[index], namespaceSegments[index + namespaceOffset], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsInsideStringLiteral(string content, int index)
    {
        int lineStart = content.LastIndexOf('\n', index > 0 ? index - 1 : 0) + 1;
        int quoteCount = 0;
        for (int i = lineStart; i < index; i++)
        {
            if (content[i] == '"' && (i == lineStart || content[i - 1] != '\\'))
            {
                quoteCount++;
            }
        }

        return quoteCount % 2 == 1;
    }

    internal static bool IsInsideLineComment(string content, int index)
    {
        int lineStart = content.LastIndexOf('\n', index > 0 ? index - 1 : 0) + 1;
        bool inString = false;
        for (int i = lineStart; i < index - 1; i++)
        {
            if (content[i] == '"' && (i == lineStart || content[i - 1] != '\\'))
            {
                inString = !inString;
            }

            if (!inString && content[i] == '/' && content[i + 1] == '/')
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsTrulyUsefulComment(string comment)
    {
        string lower = comment.ToLowerInvariant();
#if NET5_0_OR_GREATER
        if (comment.TrimStart().StartsWith('<'))
#else
        if (comment.TrimStart().StartsWith("<", StringComparison.Ordinal))
#endif
        {
            return true;
        }

#if NET5_0_OR_GREATER
        if (TrulyUsefulCommentRegex().IsMatch(comment.Trim()))
#else
        if (Regex.IsMatch(comment.Trim(), @"^(First|Second|Third|Fourth|Pass \d+|Step \d+|Phase \d+)\b", RegexOptions.IgnoreCase))
#endif
        {
            return true;
        }

        return comment.StartsWith("TODO", StringComparison.OrdinalIgnoreCase)
            || comment.StartsWith("FIXME", StringComparison.OrdinalIgnoreCase)
            || comment.StartsWith("HACK", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("why ")
            || lower.Contains("because")
            || lower.Contains("reason:")
            || lower.Contains("note:")
            || lower.Contains("intentional")
            || lower.Contains("non-obvious")
            || lower.Contains("handles")
            || lower.Contains("avoids")
            || comment.Length <= 15;
    }

    internal static bool IsUnnecessaryComment(string comment)
    {
        string lower = comment.ToLowerInvariant().TrimEnd('.', '!', '?', ':');
        string[] redundantPhrases =
        [
            "this method", "this function", "this class", "this variable",
            "this line", "increments", "decrements", "sets the", "gets the",
            "initializes the", "checks if",
            "loops through", "iterates over", "adds one to", "subtracts one",
            "as an ai", "large language model",
            "this ensures that", "this approach ensures", "it is recommended",
            "best practice", "following best practices", "in summary",
            "please note that", "note that this", "important to note",
            "the above code", "the following code", "this code does the following",
            "the purpose of this", "simply", "basically",
        ];

        return redundantPhrases.Any(phrase => lower.Contains(phrase));
    }

    internal static int GetLineNumber(string content, int index)
    {
        int line = 1;
        for (int i = 0; i < index && i < content.Length; i++)
        {
            if (content[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    internal static string GetLineAt(string content, int index)
    {
        int start = index > 0 ? content.LastIndexOf('\n', index - 1) + 1 : 0;
        int end = content.IndexOf('\n', index);
#if NET5_0_OR_GREATER
        return end < 0 ? content[start..] : content[start..end];
#else
        return end < 0 ? content.Substring(start) : content.Substring(start, end - start);
#endif
    }

    internal static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

#if NET5_0_OR_GREATER
        return string.Concat(text.AsSpan(0, maxLength - 3), "...");
#else
        return text.Substring(0, maxLength - 3) + "...";
#endif
    }

    private static (int Depth, bool FoundOpen) ScanLineForBraces(string line, int depth, bool foundOpen)
    {
        int i = 0;
        while (i < line.Length)
        {
            char ch = line[i];

            if (ch == '/' && i + 1 < line.Length && line[i + 1] == '/')
                break;

            // Double-quoted string literal — skip until closing unescaped '"'.
            if (ch == '"')
            {
                i++;
                while (i < line.Length)
                {
                    if (line[i] == '\\') { i += 2; continue; }
                    if (line[i] == '"')  { i++; break; }
                    i++;
                }
                continue;
            }

            // Single-quoted char literal — skip the literal character and closing '\''.
            if (ch == '\'')
            {
                i++;
                if (i < line.Length && line[i] == '\\') i++; // skip escape char
                if (i < line.Length) i++;                     // skip the literal character
                if (i < line.Length && line[i] == '\'') i++; // skip closing quote
                continue;
            }

            // Normal code — count braces.
            if (ch == '{') { depth++; foundOpen = true; }
            else if (ch == '}') { depth--; }

            i++;
        }

        return (depth, foundOpen);
    }

#if NET5_0_OR_GREATER
    [System.Text.RegularExpressions.GeneratedRegex(@"^(First|Second|Third|Fourth|Pass \d+|Step \d+|Phase \d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TrulyUsefulCommentRegex();
#endif
}
