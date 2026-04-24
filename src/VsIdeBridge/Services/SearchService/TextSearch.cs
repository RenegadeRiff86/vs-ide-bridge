using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.FindResults;
using Microsoft.VisualStudio.Text;
using EnvDTE;
using EnvDTE80;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Services;

internal sealed partial class SearchService
{
    private sealed record SearchFileSnapshot(string Path, string ProjectUniqueName, string[] Lines);

    private async Task PopulateFindResultsAsync(
        IdeCommandContext context,
        IReadOnlyDictionary<string, List<FindResult>> groupedMatches,
        string query,
        int resultsWindow)
    {
        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);

            if (await context.Package.GetServiceAsync(typeof(SVsFindResults)).ConfigureAwait(true) is not IFindResultsService service)
            {
                return;
            }

            string title = $"IDE Bridge Find Results {resultsWindow}";
            string description = $"Find all \"{query}\"";
            string identifier = $"VsIdeBridge.FindResults.{resultsWindow}";
            IFindResultsWindow2 window = service.StartSearch(title, description, identifier);
            foreach (KeyValuePair<string, List<FindResult>> resultGroup in groupedMatches)
            {
                window.AddResults(resultGroup.Key, resultGroup.Key, null, resultGroup.Value);
            }

            window.Summary = $"Matching lines: {groupedMatches.Sum(resultGroup => resultGroup.Value.Count)} Matching files: {groupedMatches.Count}";
            window.Complete();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (COMException ex)
        {
            BridgeActivityLog.LogWarning(nameof(SearchService), "Failed to populate the Find Results window", ex);
        }
        catch (InvalidOperationException ex)
        {
            BridgeActivityLog.LogWarning(nameof(SearchService), "Failed to complete Find Results window population", ex);
        }
    }

    private static Regex BuildRegex(string query, bool matchCase, bool wholeWord, bool useRegex)
    {
        string pattern = useRegex ? query : Regex.Escape(query);
        if (wholeWord)
        {
            pattern = $@"\b{pattern}\b";
        }

        RegexOptions options = RegexOptions.Compiled;
        if (!matchCase)
        {
            options |= RegexOptions.IgnoreCase;
        }

        return new Regex(pattern, options);
    }

    private async Task<(List<SearchHit> Matches, Dictionary<string, List<FindResult>> GroupedMatches)> SearchTextMatchesAsync(
        IdeCommandContext context,
        string query,
        string scope,
        bool matchCase,
        bool wholeWord,
        bool useRegex,
        string? projectUniqueName,
        string? pathFilter = null)
    {
        List<SearchFileSnapshot> files = await CaptureSearchFileSnapshotsAsync(context, scope, projectUniqueName, pathFilter).ConfigureAwait(true);
        return await SearchTextMatchesAsync(files, query, matchCase, wholeWord, useRegex, context.CancellationToken).ConfigureAwait(false);
    }

    private static Task<(List<SearchHit> Matches, Dictionary<string, List<FindResult>> GroupedMatches)> SearchTextMatchesAsync(
        IReadOnlyList<SearchFileSnapshot> files,
        string query,
        bool matchCase,
        bool wholeWord,
        bool useRegex,
        CancellationToken cancellationToken)
    {
        Regex regex = BuildRegex(query, matchCase, wholeWord, useRegex);
        return Task.Run(() => SearchTextMatchesInSnapshots(files, regex, query, cancellationToken), cancellationToken);
    }

    private async Task<List<SearchFileSnapshot>> CaptureSearchFileSnapshotsAsync(
        IdeCommandContext context,
        string scope,
        string? projectUniqueName,
        string? pathFilter)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);

        string? normalizedPathFilter = NormalizeSearchPathFilter(context.Dte, pathFilter);

        (string Path, string ProjectUniqueName)[] allFiles = scope switch
        {
            "document" => [await GetDocumentTargetAsync(context, normalizedPathFilter).ConfigureAwait(true)],
            "open" => [.. EnumerateOpenFiles(context.Dte)],
            "project" => [.. EnumerateSolutionFiles(context.Dte).Where(item => string.Equals(item.ProjectUniqueName, projectUniqueName, StringComparison.OrdinalIgnoreCase))],
            _ => [.. EnumerateSolutionFiles(context.Dte)],
        };

        (string Path, string ProjectUniqueName)[] files = string.IsNullOrWhiteSpace(normalizedPathFilter)
            ? allFiles
            : [.. allFiles.Where(file => MatchesPathFilter(file.Path, normalizedPathFilter))];

        Dictionary<string, string[]> openDocumentSnapshots = CaptureOpenDocumentSnapshots(context.Dte, files.Select(file => file.Path));
        return await Task.Run(
            () => BuildSearchFileSnapshots(files, openDocumentSnapshots, context.CancellationToken),
            context.CancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, string[]> CaptureOpenDocumentSnapshots(DTE2 dte, IEnumerable<string> targetPaths)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        IEnumerable<string> normalizedTargetPaths = targetPaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(PathNormalization.NormalizeFilePath);

        HashSet<string> normalizedTargets = new(normalizedTargetPaths, StringComparer.OrdinalIgnoreCase);

        Dictionary<string, string[]> snapshots = new(StringComparer.OrdinalIgnoreCase);
        if (normalizedTargets.Count == 0)
        {
            return snapshots;
        }

        foreach (Document document in dte.Documents)
        {
            try
            {
                string normalizedPath = PathNormalization.NormalizeFilePath(document.FullName);
                if (!normalizedTargets.Contains(normalizedPath))
                {
                    continue;
                }

                if (document.Object("TextDocument") is TextDocument textDocument)
                {
                    EditPoint editPoint = textDocument.StartPoint.CreateEditPoint();
                    string text = editPoint.GetText(textDocument.EndPoint);
                    snapshots[normalizedPath] = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                }
            }
            catch (COMException ex)
            {
                TraceSearchFailure("CaptureOpenDocumentSnapshots", ex);
            }
        }

        return snapshots;
    }

    private static (List<SearchHit> Matches, Dictionary<string, List<FindResult>> GroupedMatches) SearchTextMatchesInSnapshots(
        IReadOnlyList<SearchFileSnapshot> files,
        Regex regex,
        string query,
        CancellationToken cancellationToken)
    {
        List<SearchHit> hits = [];
        Dictionary<string, List<FindResult>> groupedMatches = [];

        foreach (SearchFileSnapshot file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            for (int lineIndex = 0; lineIndex < file.Lines.Length; lineIndex++)
            {
                string line = file.Lines[lineIndex];
                foreach (Match match in regex.Matches(line))
                {
                    hits.Add(new SearchHit
                    {
                        Path = file.Path,
                        ProjectUniqueName = file.ProjectUniqueName,
                        Line = lineIndex + 1,
                        Column = match.Index + 1,
                        MatchLength = match.Length,
                        Preview = line,
                        ScoreHint = 0,
                        SourceQueries = [query],
                    });

                    if (!groupedMatches.TryGetValue(file.Path.ToLowerInvariant(), out List<FindResult>? results))
                    {
                        results = [];
                        groupedMatches[file.Path.ToLowerInvariant()] = results;
                    }

                    results.Add(new FindResult(line, lineIndex, match.Index, new Span(match.Index, match.Length)));
                }
            }
        }

        return (hits, groupedMatches);
    }

    private static List<SearchFileSnapshot> BuildSearchFileSnapshots(
        IReadOnlyList<(string Path, string ProjectUniqueName)> files,
        IReadOnlyDictionary<string, string[]> openDocumentSnapshots,
        CancellationToken cancellationToken)
    {
        List<SearchFileSnapshot> snapshots = [];

        foreach ((string Path, string ProjectUniqueName) file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(file.Path) || !File.Exists(file.Path))
            {
                continue;
            }

            string normalizedPath = PathNormalization.NormalizeFilePath(file.Path);
            if (!openDocumentSnapshots.TryGetValue(normalizedPath, out string[]? lines))
            {
                lines = File.ReadAllLines(file.Path);
            }

            snapshots.Add(new SearchFileSnapshot(file.Path, file.ProjectUniqueName, lines));
        }

        return snapshots;
    }

    private static (List<SearchHit> Matches, Dictionary<string, List<FindResult>> GroupedMatches) SearchSmartQueryTermsInSnapshots(
        IReadOnlyList<SearchFileSnapshot> files,
        IReadOnlyList<SmartQueryTerm> terms,
        CancellationToken cancellationToken)
    {
        Dictionary<string, SearchHit> hitMap = [];
        Dictionary<string, List<FindResult>> groupedMatches = [];

        foreach (SmartQueryTerm term in terms)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Regex regex = BuildRegex(term.Text, matchCase: false, wholeWord: term.WholeWord, useRegex: false);
            (List<SearchHit> Matches, _) = SearchTextMatchesInSnapshots(files, regex, term.Text, cancellationToken);

            foreach (SearchHit hit in Matches)
            {
                string key = $"{hit.Path.ToLowerInvariant()}|{hit.Line}|{hit.Column}";

                if (!hitMap.TryGetValue(key, out SearchHit? existing))
                {
                    existing = new SearchHit
                    {
                        Path = hit.Path,
                        ProjectUniqueName = hit.ProjectUniqueName,
                        Line = hit.Line,
                        Column = hit.Column,
                        MatchLength = hit.MatchLength,
                        Preview = hit.Preview,
                        ScoreHint = 0,
                    };
                    hitMap[key] = existing;
                }

                existing.ScoreHint += term.Weight;
                if (!existing.SourceQueries.Any(query => string.Equals(query, term.Text, StringComparison.OrdinalIgnoreCase)))
                {
                    existing.SourceQueries.Add(term.Text);
                }

                if (!groupedMatches.TryGetValue(hit.Path.ToLowerInvariant(), out List<FindResult>? results))
                {
                    results = [];
                    groupedMatches[hit.Path.ToLowerInvariant()] = results;
                }

                results.Add(new FindResult(hit.Preview, hit.Line - 1, hit.Column - 1, new Span(hit.Column - 1, hit.MatchLength)));
            }
        }

        return (hitMap.Values
            .OrderByDescending(hit => hit.ScoreHint)
            .ThenBy(hit => hit.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(hit => hit.Line)
            .ToList(), groupedMatches);
    }

    private async Task<(List<SearchHit> Matches, Dictionary<string, List<FindResult>> GroupedMatches)> SearchSmartQueryTermsAsync(
        IdeCommandContext context,
        string query,
        string scope,
        string? projectUniqueName)
    {
        IReadOnlyList<SmartQueryTerm> terms = [.. ExtractSmartQueryTerms(query).Take(SmartContextMaxQueryTerms)];
        List<SearchFileSnapshot> files = await CaptureSearchFileSnapshotsAsync(context, scope, projectUniqueName, pathFilter: null).ConfigureAwait(true);
        return await Task.Run(
            () => SearchSmartQueryTermsInSnapshots(files, terms, context.CancellationToken),
            context.CancellationToken).ConfigureAwait(false);
    }

    private static List<string> NormalizeQueries(IEnumerable<string> queries)
    {
        List<string> normalized = [];
        HashSet<string> seen = [];

        foreach (string? query in queries)
        {
            string? trimmed = query?.Trim();
            if (trimmed is not { Length: > 0 })
            {
                continue;
            }

            if (!seen.Add(trimmed.ToLowerInvariant()))
            {
                continue;
            }

            normalized.Add(trimmed);
        }

        return normalized;
    }

    private static void MergeSearchHits(Dictionary<string, SearchHit> mergedHits, IEnumerable<SearchHit> hits)
    {
        foreach (SearchHit hit in hits)
        {
            string key = GetSearchHitKey(hit);
            if (!mergedHits.TryGetValue(key, out SearchHit? existing))
            {
                mergedHits[key] = new SearchHit
                {
                    Path = hit.Path,
                    ProjectUniqueName = hit.ProjectUniqueName,
                    Line = hit.Line,
                    Column = hit.Column,
                    MatchLength = hit.MatchLength,
                    Preview = hit.Preview,
                    ScoreHint = hit.ScoreHint,
                    SourceQueries = [.. hit.SourceQueries],
                };
                continue;
            }

            existing.ScoreHint = Math.Max(existing.ScoreHint, hit.ScoreHint);
            foreach (string query in hit.SourceQueries)
            {
                if (!existing.SourceQueries.Any(sourceQuery => string.Equals(sourceQuery, query, StringComparison.OrdinalIgnoreCase)))
                {
                    existing.SourceQueries.Add(query);
                }
            }
        }
    }

    private static Dictionary<string, List<FindResult>> BuildGroupedMatchesFromHits(IEnumerable<SearchHit> hits)
    {
        Dictionary<string, List<FindResult>> groupedMatches = [];
        foreach (SearchHit hit in hits)
        {
            if (!groupedMatches.TryGetValue(hit.Path.ToLowerInvariant(), out List<FindResult>? results))
            {
                results = [];
                groupedMatches[hit.Path.ToLowerInvariant()] = results;
            }

            results.Add(new FindResult(hit.Preview, hit.Line - 1, hit.Column - 1, new Span(hit.Column - 1, hit.MatchLength)));
        }

        return groupedMatches;
    }

    private static string GetSearchHitKey(SearchHit hit)
    {
        return $"{hit.Path.ToLowerInvariant()}|{hit.Line}|{hit.Column}|{hit.MatchLength}";
    }
}
