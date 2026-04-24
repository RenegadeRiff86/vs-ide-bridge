using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace VsIdeBridge.Services;

internal sealed partial class DocumentService
{
    private const int MaxSemanticOutlineSymbols = 500;
    private static readonly HashSet<string> s_textOutlineExtensions =
    [
        ".c",
        ".cc",
        ".cpp",
        ".cxx",
        ".h",
        ".hh",
        ".hpp",
        ".hxx",
        ".ipp",
        ".inl",
        ".ixx",
    ];

    private static readonly HashSet<vsCMElement> s_outlineKinds =
    [
        vsCMElement.vsCMElementFunction,
        vsCMElement.vsCMElementClass,
        vsCMElement.vsCMElementStruct,
        vsCMElement.vsCMElementEnum,
        vsCMElement.vsCMElementNamespace,
        vsCMElement.vsCMElementInterface,
        vsCMElement.vsCMElementProperty,
        vsCMElement.vsCMElementVariable,
    ];

    public async Task<JObject> GetFileOutlineAsync(DTE2 dte, string? filePath, int maxDepth, string? kindFilter = null)
    {
        (string resolvedPath, JArray symbols, int count, string? note) =
            await GetOutlineDataOnMainThreadAsync(dte, filePath, maxDepth, kindFilter).ConfigureAwait(false);

        JObject outlineResult = new()
        {
            [ResolvedPathProperty] = resolvedPath,
            ["count"] = count,
            ["symbols"] = symbols,
        };
        if (note is not null) outlineResult["note"] = note;
        return outlineResult;
    }

    private static async Task<(string ResolvedPath, JArray Symbols, int Count, string? Note)> GetOutlineDataOnMainThreadAsync(
        DTE2 dte,
        string? filePath,
        int maxDepth,
        string? kindFilter)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        string resolvedPath = ResolveDocumentPath(dte, filePath);

        if (ShouldUseTextOutlineFallback(resolvedPath))
        {
            JArray textSymbols = BuildTextOutlineFallback(resolvedPath, maxDepth, kindFilter);
            return (resolvedPath, textSymbols, textSymbols.Count, "Returned a text outline fallback for a C/C++ file to avoid blocking the Visual Studio code model.");
        }

        ProjectItem? projectItem = null;
        try { projectItem = dte.Solution.FindProjectItem(resolvedPath); } catch (COMException ex) { System.Diagnostics.Debug.WriteLine(ex); }

        JArray symbols = [];
        string? note = projectItem is null
            ? "File is not part of any project or code model is unavailable."
            : TryCollectFileCodeElements(projectItem, symbols, maxDepth, kindFilter);

        if (symbols.Count == 0 && File.Exists(resolvedPath))
        {
            JArray fallbackSymbols = BuildTextOutlineFallback(resolvedPath, maxDepth, kindFilter);
            if (fallbackSymbols.Count > 0)
            {
                return (resolvedPath, fallbackSymbols, fallbackSymbols.Count, $"{note ?? "Code model unavailable."} Returned a text outline fallback instead.");
            }
        }

        int count = symbols.Count;
        await Task.Yield();
        return (resolvedPath, symbols, count, note);
    }

    private static string? TryCollectFileCodeElements(ProjectItem projectItem, JArray symbols, int maxDepth, string? kindFilter)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            FileCodeModel? codeModel = projectItem.FileCodeModel;
            if (codeModel?.CodeElements is null)
            {
                return "No code model available for this file type.";
            }

            return TryCollectOutlineFromCodeElements(codeModel.CodeElements, symbols, maxDepth, kindFilter);
        }
        catch (COMException ex)
        {
            return $"Code model unavailable: {ex.Message}";
        }
    }

    private static string? TryCollectOutlineFromCodeElements(CodeElements codeElements, JArray symbols, int maxDepth, string? kindFilter)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        int symbolCount = 0;
        foreach (CodeElement element in codeElements)
        {
            if (TryCollectOutlineFromCodeElement(element, symbols, maxDepth, kindFilter, ref symbolCount))
            {
                return $"Outline truncated after {MaxSemanticOutlineSymbols} symbols to keep Visual Studio responsive.";
            }
        }

        return null;
    }

    private static bool TryCollectOutlineFromCodeElement(CodeElement element, JArray symbols, int maxDepth, string? kindFilter, ref int symbolCount)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            return CollectOutlineSymbols(element, symbols, 0, maxDepth, kindFilter, ref symbolCount);
        }
        catch (COMException ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return false;
        }
    }

    private static bool CollectOutlineSymbols(CodeElement element, JArray symbols, int depth, int maxDepth, string? kindFilter, ref int symbolCount)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (depth > maxDepth || symbolCount >= MaxSemanticOutlineSymbols) return symbolCount >= MaxSemanticOutlineSymbols;

        vsCMElement kind;
        try { kind = element.Kind; } catch (COMException ex) { System.Diagnostics.Debug.WriteLine(ex); return false; }

        string name = string.Empty;
        int startLine = 0, endLine = 0;
        try { name = element.Name ?? string.Empty; } catch (COMException ex) { System.Diagnostics.Debug.WriteLine(ex); }
        try { startLine = element.StartPoint?.Line ?? 0; } catch (COMException ex) { System.Diagnostics.Debug.WriteLine(ex); }
        try { endLine = element.EndPoint?.Line ?? 0; } catch (COMException ex) { System.Diagnostics.Debug.WriteLine(ex); }

        if (s_outlineKinds.Contains(kind))
        {
            string kindName = NormalizeOutlineKind(kind);
            bool matchesFilter = MatchesOutlineKind(kindName, kindFilter);
            if (matchesFilter)
            {
                symbols.Add(new JObject
                {
                    ["name"] = name,
                    ["kind"] = kindName,
                    ["startLine"] = startLine,
                    ["endLine"] = endLine,
                    ["depth"] = depth,
                });
                symbolCount++;
                if (symbolCount >= MaxSemanticOutlineSymbols)
                {
                    return true;
                }
            }
        }

        CodeElements? children = null;
        try
        {
            if (element is CodeNamespace ns) children = ns.Members;
            else if (element is CodeClass cls) children = cls.Members;
            else if (element is CodeStruct st) children = st.Members;
            else if (element is CodeInterface iface) children = iface.Members;
        }
        catch (COMException ex) { System.Diagnostics.Debug.WriteLine(ex); }

        if (children is null) return false;
        foreach (CodeElement child in children)
        {
            try
            {
                if (CollectOutlineSymbols(child, symbols, depth + 1, maxDepth, kindFilter, ref symbolCount))
                {
                    return true;
                }
            }
            catch (COMException ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }

        return false;
    }

    private static bool ShouldUseTextOutlineFallback(string resolvedPath)
    {
        string extension = Path.GetExtension(resolvedPath);
        return s_textOutlineExtensions.Contains(extension);
    }

    private static JArray BuildTextOutlineFallback(string resolvedPath, int maxDepth, string? kindFilter)
    {
        JArray symbols = [];
        if (!File.Exists(resolvedPath))
        {
            return symbols;
        }

        string[] lines = File.ReadAllLines(resolvedPath);
        int braceDepth = 0;
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            string trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                braceDepth += CountBraces(line);
                continue;
            }

            int symbolDepth = Math.Max(0, braceDepth);
            if (TryParseTextOutlineSymbol(trimmed, index + 1, symbolDepth, kindFilter) is JObject symbol && symbolDepth <= maxDepth)
            {
                symbols.Add(symbol);
            }

            braceDepth += CountBraces(line);
        }

        return symbols;
    }

    private static JObject? TryParseTextOutlineSymbol(string trimmedLine, int lineNumber, int depth, string? kindFilter)
    {
        string? keywordKind = TryMatchKeywordKind(trimmedLine, out string? name);
        if (keywordKind is not null && MatchesOutlineKind(keywordKind, kindFilter))
        {
            return CreateTextOutlineSymbol(name ?? string.Empty, keywordKind, lineNumber, depth);
        }

        if (LooksLikeTextFunction(trimmedLine, out string? functionName) && MatchesOutlineKind("function", kindFilter))
        {
            return CreateTextOutlineSymbol(functionName ?? string.Empty, "function", lineNumber, depth);
        }

        return null;
    }

    private static JObject CreateTextOutlineSymbol(string name, string kind, int lineNumber, int depth)
    {
        return new JObject
        {
            ["name"] = name,
            ["kind"] = kind,
            ["startLine"] = lineNumber,
            ["endLine"] = lineNumber,
            ["depth"] = depth,
        };
    }

    private static string? TryMatchKeywordKind(string trimmedLine, out string? name)
    {
        name = null;
        string[] keywords = ["enum class", "namespace", "class", "struct", "enum"];
        foreach (string keyword in keywords)
        {
            if (!trimmedLine.StartsWith(keyword + " ", StringComparison.Ordinal))
            {
                continue;
            }

            string remainder = trimmedLine.Substring(keyword.Length).Trim();
            if (string.IsNullOrWhiteSpace(remainder))
            {
                continue;
            }

            int delimiter = remainder.IndexOfAny([' ', ':', '{']);
            name = delimiter >= 0 ? remainder.Substring(0, delimiter) : remainder;
            return keyword.StartsWith("enum", StringComparison.Ordinal) ? "enum" : keyword;
        }

        return null;
    }

    private static bool LooksLikeTextFunction(string trimmedLine, out string? functionName)
    {
        functionName = null;
        int openParen = trimmedLine.IndexOf('(');
        int closeParen = trimmedLine.LastIndexOf(')');
        if (openParen <= 0 || closeParen <= openParen)
        {
            return false;
        }

        string beforeParen = trimmedLine.Substring(0, openParen).TrimEnd();
        if (beforeParen.Length == 0)
        {
            return false;
        }

        string candidate = ExtractTrailingQualifiedName(beforeParen);
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        int qualifierIndex = candidate.LastIndexOf("::", StringComparison.Ordinal);
        string bareName = qualifierIndex >= 0
            ? candidate.Substring(qualifierIndex + 2)
            : candidate;

        if (bareName is "if" or "for" or "while" or "switch" or "catch")
        {
            return false;
        }

        functionName = candidate;
        return true;
    }

    private static string ExtractTrailingQualifiedName(string text)
    {
        Match match = Regex.Match(text, @"(?<name>[~A-Za-z_][A-Za-z0-9_:~]*)\s*$");
        return match.Success ? match.Groups["name"].Value : string.Empty;
    }

    private static int CountBraces(string line)
    {
        int count = 0;
        foreach (char character in line)
        {
            if (character == '{')
            {
                count++;
            }
            else if (character == '}')
            {
                count--;
            }
        }

        return count;
    }

    private static string NormalizeOutlineKind(vsCMElement kind)
    {
        return kind switch
        {
            vsCMElement.vsCMElementFunction => "function",
            vsCMElement.vsCMElementClass => "class",
            vsCMElement.vsCMElementStruct => "struct",
            vsCMElement.vsCMElementEnum => "enum",
            vsCMElement.vsCMElementNamespace => "namespace",
            vsCMElement.vsCMElementInterface => "interface",
            vsCMElement.vsCMElementProperty => "member",
            vsCMElement.vsCMElementVariable => "member",
            _ => kind.ToString().Replace("vsCMElement", string.Empty),
        };
    }

    private static bool MatchesOutlineKind(string normalizedKind, string? kindFilter)
    {
        string? filter = kindFilter?.Trim();
        if (string.IsNullOrWhiteSpace(filter) ||
            string.Equals(filter, "all", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return filter!.ToLowerInvariant() switch
        {
            "type" => normalizedKind is "class" or "struct" or "enum" or "interface",
            "member" => normalizedKind is "member" or "function",
            _ => normalizedKind.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0,
        };
    }
}
