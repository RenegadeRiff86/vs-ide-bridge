using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Services;

internal sealed partial class DocumentService
{
    private readonly struct DocumentFileMatch(string path, int score, string source)
    {
        public string Path { get; } = path;
        public int Score { get; } = score;
        public string Source { get; } = source;
    }

    private static string ResolveDocumentPath(DTE2 dte, string? filePath, bool allowDiskFallback = true)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            return ResolveRequestedDocumentPath(dte, filePath!, allowDiskFallback);
        }

        if (dte.ActiveDocument is null || string.IsNullOrWhiteSpace(dte.ActiveDocument.FullName))
        {
            throw new CommandErrorException(DocumentNotFoundCode, "There is no active document.");
        }

        return PathNormalization.NormalizeFilePath(dte.ActiveDocument.FullName);
    }

    private static string ResolveRequestedDocumentPath(DTE2 dte, string filePath, bool allowDiskFallback)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!Path.IsPathRooted(filePath) && TryResolveRelativeDocumentPath(dte, filePath) is { } resolvedRelativePath)
        {
            return resolvedRelativePath;
        }

        string normalizedPath = PathNormalization.NormalizeFilePath(filePath);
        if (File.Exists(normalizedPath))
        {
            return normalizedPath;
        }

        DocumentFileMatch[] allMatches = [..SolutionFileLocator.FindMatches(dte, filePath)
            .Concat(allowDiskFallback ? SolutionFileLocator.FindDiskMatches(dte, filePath, maxResults: 250) : [])
            .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => new DocumentFileMatch(
                group.Key,
                group.Max(item => item.Score),
                group.OrderByDescending(item => item.Score).First().Source))
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Path.Length)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            ];

        if (allMatches.Length == 0)
        {
            throw new CommandErrorException(
                DocumentNotFoundCode,
                $"File not found: {normalizedPath}. No solution item matched '{filePath}'.");
        }

        return SelectBestMatchOrThrow(allMatches, filePath);
    }

    private static string SelectBestMatchOrThrow(DocumentFileMatch[] allMatches, string filePath)
    {
        int topScore = allMatches[0].Score;
        DocumentFileMatch[] topMatches = [..allMatches
            .Where(item => item.Score == topScore)];
        int? secondScore = allMatches.Length > topMatches.Length ? (int?)allMatches[topMatches.Length].Score : null;
        bool clearLead = secondScore is null || topScore - secondScore.Value >= 50;

        if (topMatches.Length == 1 && topScore >= 900 && clearLead)
        {
            return topMatches[0].Path;
        }

        string preview = string.Join(", ", allMatches.Take(5).Select(item => $"{item.Path} ({item.Source}, score={item.Score})"));
        string suffix = allMatches.Length > 5 ? ", ..." : string.Empty;
        throw new CommandErrorException(
            "document_ambiguous",
            $"Multiple solution items matched '{filePath}'. Use find-files to disambiguate. Candidates: {preview}{suffix}");
    }

    private static string? TryResolveRelativeDocumentPath(DTE2 dte, string filePath)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        string normalizedRelativePath = filePath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);

        List<string> rootedCandidates = [];

        foreach (string root in EnumerateDocumentSearchRoots(dte))
        {
            string candidate = PathNormalization.NormalizeFilePath(Path.Combine(root, normalizedRelativePath));
            if (File.Exists(candidate) && !rootedCandidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                rootedCandidates.Add(candidate);
            }
        }

        if (rootedCandidates.Count > 0)
        {
            return rootedCandidates
                .OrderByDescending(path => IsSourceTreePath(path))
                .ThenBy(path => IsBuildArtifactPath(path))
                .ThenBy(path => path.Length)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .First();
        }

        string targetFileName = Path.GetFileName(normalizedRelativePath);
        List<string> matchingPaths = [];
        foreach (Document document in EnumerateOpenDocuments(dte))
        {
            string? documentPath = TryGetDocumentFullName(document);
            if (string.IsNullOrWhiteSpace(documentPath))
            {
                continue;
            }

            string normalizedDocumentPath = PathNormalization.NormalizeFilePath(documentPath);
            string comparableDocumentPath = normalizedDocumentPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            if (comparableDocumentPath.EndsWith(Path.DirectorySeparatorChar + normalizedRelativePath, StringComparison.OrdinalIgnoreCase) ||
                comparableDocumentPath.EndsWith(normalizedRelativePath, StringComparison.OrdinalIgnoreCase))
            {
                matchingPaths.Add(normalizedDocumentPath);
                continue;
            }

            if (string.Equals(Path.GetFileName(comparableDocumentPath), targetFileName, StringComparison.OrdinalIgnoreCase))
            {
                matchingPaths.Add(normalizedDocumentPath);
            }
        }

        if (matchingPaths.Count == 0)
        {
            return null;
        }

        return matchingPaths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(path => path.EndsWith(Path.DirectorySeparatorChar + normalizedRelativePath, StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(normalizedRelativePath, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(path => IsSourceTreePath(path))
            .ThenBy(path => IsBuildArtifactPath(path))
            .ThenBy(path => path.Length)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    private static IReadOnlyList<string> EnumerateDocumentSearchRoots(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        List<string> searchRoots = [];
        string solutionPath = dte.Solution?.IsOpen == true ? dte.Solution.FullName : string.Empty;
        string current = string.IsNullOrWhiteSpace(solutionPath)
            ? string.Empty
            : Path.GetDirectoryName(PathNormalization.NormalizeFilePath(solutionPath)) ?? string.Empty;

        while (!string.IsNullOrWhiteSpace(current))
        {
            AddDistinctPath(searchRoots, current);
            string parent = Path.GetDirectoryName(current) ?? string.Empty;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }

        return searchRoots;
    }

    private static void AddDistinctPath(List<string> searchRoots, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return;
        }

        string normalizedCandidate = PathNormalization.NormalizeFilePath(candidate);
        if (!searchRoots.Contains(normalizedCandidate, StringComparer.OrdinalIgnoreCase))
        {
            searchRoots.Add(normalizedCandidate);
        }
    }

    private static (List<Document> Documents, string MatchedBy) ResolveDocumentMatches(
        DTE2 dte,
        string? query,
        bool fallbackToActive,
        bool allowMultiple)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        IReadOnlyList<Document> documents = EnumerateOpenDocuments(dte);
        if (documents.Count == 0)
        {
            throw new CommandErrorException(DocumentNotFoundCode, "There are no open documents.");
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return ResolveMatchesForEmptyQuery(dte, documents, fallbackToActive, allowMultiple);
        }

        return FindDocumentsByQuery(dte, documents, query!.Trim(), allowMultiple);
    }

    private static (List<Document> Documents, string MatchedBy) ResolveMatchesForEmptyQuery(
        DTE2 dte,
        IReadOnlyList<Document> documents,
        bool fallbackToActive,
        bool allowMultiple)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (allowMultiple)
        {
            return (documents.ToList(), "all");
        }

        if (!fallbackToActive || dte.ActiveDocument is null)
        {
            throw new CommandErrorException("invalid_arguments", "Missing document query.");
        }

        return (new List<Document> { dte.ActiveDocument }, "active");
    }

    private static (List<Document> Documents, string MatchedBy) FindDocumentsByQuery(
        DTE2 dte,
        IReadOnlyList<Document> documents,
        string rawQuery,
        bool allowMultiple)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        bool queryLooksLikePath = rawQuery.IndexOfAny(['\\', '/', ':']) >= 0;
        if (queryLooksLikePath)
        {
            string normalizedQueryPath = PathNormalization.NormalizeFilePath(rawQuery);
            List<Document> exactPath = [..documents.Where(document => MatchesDocumentExactPath(document, normalizedQueryPath))];
            if (exactPath.Count > 0)
            {
                return FinalizeMatches(exactPath, allowMultiple, "path");
            }
        }

        List<Document> exactName = [..documents.Where(document => MatchesDocumentExactName(document, rawQuery))];
        if (exactName.Count > 0)
        {
            exactName = PreferSingleDocumentMatch(dte, exactName);
            return FinalizeMatches(exactName, allowMultiple, "filename");
        }

        List<Document> containsName = [..documents.Where(document => MatchesDocumentNameContains(document, rawQuery))];
        if (containsName.Count > 0)
        {
            containsName = PreferSingleDocumentMatch(dte, containsName);
            return FinalizeMatches(containsName, allowMultiple, "filename-contains");
        }

        List<Document> containsPath = [..documents.Where(document => MatchesDocumentPathContains(document, rawQuery))];
        if (containsPath.Count > 0)
        {
            containsPath = PreferSingleDocumentMatch(dte, containsPath);
            return FinalizeMatches(containsPath, allowMultiple, "path-contains");
        }

        throw new CommandErrorException(DocumentNotFoundCode, $"No open document matched '{rawQuery}'.");
    }

    private static List<Document> PreferSingleDocumentMatch(DTE2 dte, List<Document> matches)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (matches.Count <= 1)
        {
            return matches;
        }

        if (TryGetDocumentFullName(dte.ActiveDocument) is string activeDocumentPath &&
            !string.IsNullOrWhiteSpace(activeDocumentPath))
        {
            List<Document> activeMatches = [..matches.Where(document => MatchesDocumentExactPath(document, activeDocumentPath))];
            if (activeMatches.Count == 1)
            {
                return activeMatches;
            }
        }

        matches = PreferMatches(matches, document => TryGetDocumentFullName(document) is string path && IsSourceTreePath(path));
        matches = PreferMatches(matches, document => TryGetDocumentFullName(document) is string path && !IsBuildArtifactPath(path));
        matches = PreferMatches(matches, IsProjectBackedDocument);
        matches = PreferMatches(matches, document => !IsReviewArtifactDocument(document));
        matches = PreferMatches(matches, document => !IsOutputDocument(document));
        return matches;
    }

    private static List<Document> PreferMatches(List<Document> matches, Func<Document, bool> predicate)
    {
        List<Document> preferred = [..matches.Where(predicate)];
        return preferred.Count == 0 ? matches : preferred;
    }

    private static (List<Document> Documents, string MatchedBy) FinalizeMatches(List<Document> matches, bool allowMultiple, string matchedBy)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!allowMultiple && matches.Count > 1)
        {
            string options = string.Join(", ", GetDistinctDocumentNames(matches));
            throw new CommandErrorException("invalid_arguments", $"Document query is ambiguous. Matches: {options}");
        }

        return allowMultiple ? (matches, matchedBy) : ([matches[0]], matchedBy);
    }

    private static Document? TryFindOpenDocumentByPath(DTE2 dte, string resolvedPath)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        foreach (Document document in dte.Documents)
        {
            if (MatchesDocumentExactPath(document, resolvedPath))
            {
                return document;
            }
        }

        return null;
    }

    private static string? TryFindExistingOpenDocumentPathByFileName(DTE2 dte, string filePath)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        string fileName = Path.GetFileName(filePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        List<string> matchingPaths = [];
        foreach (Document document in EnumerateOpenDocuments(dte))
        {
            string? documentPath = TryGetDocumentFullName(document);
            if (string.IsNullOrWhiteSpace(documentPath) || !File.Exists(documentPath))
            {
                continue;
            }

            if (string.Equals(Path.GetFileName(documentPath), fileName, StringComparison.OrdinalIgnoreCase))
            {
                matchingPaths.Add(documentPath!);
            }
        }

        if (matchingPaths.Count == 0)
        {
            return null;
        }

        return matchingPaths
            .OrderByDescending(path => IsSourceTreePath(path))
            .ThenBy(path => IsBuildArtifactPath(path))
            .ThenBy(path => path.Length)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    private static bool IsSourceTreePath(string path)
    {
        string normalizedPath = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return normalizedPath.IndexOf("\\src\\", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsBuildArtifactPath(string path)
    {
        string normalizedPath = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return normalizedPath.IndexOf("\\build\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
            normalizedPath.IndexOf("\\bin\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
            normalizedPath.IndexOf("\\obj\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
            normalizedPath.IndexOf("\\out\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
            normalizedPath.IndexOf("\\output\\", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
