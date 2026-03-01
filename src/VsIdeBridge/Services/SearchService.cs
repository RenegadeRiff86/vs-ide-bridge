using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.FindResults;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using Newtonsoft.Json.Linq;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Services;

internal sealed class SearchService
{
    public async Task<JObject> FindFilesAsync(IdeCommandContext context, string query)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);

        var matches = new JArray(
            EnumerateSolutionFiles(context.Dte)
                .Where(item => item.Path.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                               Path.GetFileName(item.Path).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(item => new JObject
                {
                    ["path"] = item.Path,
                    ["project"] = item.ProjectUniqueName,
                }));

        return new JObject
        {
            ["query"] = query,
            ["count"] = matches.Count,
            ["matches"] = matches,
        };
    }

    public async Task<JObject> FindTextAsync(
        IdeCommandContext context,
        string query,
        string scope,
        bool matchCase,
        bool wholeWord,
        bool useRegex,
        int resultsWindow,
        string? projectUniqueName)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);

        var files = scope switch
        {
            "document" => new[] { await GetDocumentTargetAsync(context).ConfigureAwait(true) },
            "project" => EnumerateSolutionFiles(context.Dte)
                .Where(item => string.Equals(item.ProjectUniqueName, projectUniqueName, StringComparison.OrdinalIgnoreCase))
                .ToArray(),
            _ => EnumerateSolutionFiles(context.Dte).ToArray(),
        };

        var regex = BuildRegex(query, matchCase, wholeWord, useRegex);
        var matchesJson = new JArray();
        var groupedMatches = new Dictionary<string, List<FindResult>>();

        foreach (var file in files)
        {
            if (!File.Exists(file.Path))
            {
                continue;
            }

            var lines = File.ReadAllLines(file.Path);
            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                var line = lines[lineIndex];
                foreach (Match match in regex.Matches(line))
                {
                    matchesJson.Add(new JObject
                    {
                        ["path"] = file.Path,
                        ["project"] = file.ProjectUniqueName,
                        ["line"] = lineIndex + 1,
                        ["column"] = match.Index + 1,
                        ["matchLength"] = match.Length,
                        ["preview"] = line,
                    });

                    if (!groupedMatches.TryGetValue(file.Path, out var results))
                    {
                        results = new List<FindResult>();
                        groupedMatches[file.Path] = results;
                    }

                    results.Add(new FindResult(line, lineIndex, match.Index, new Span(match.Index, match.Length)));
                }
            }
        }

        await PopulateFindResultsAsync(context, groupedMatches, query, resultsWindow).ConfigureAwait(true);

        return new JObject
        {
            ["query"] = query,
            ["scope"] = scope,
            ["count"] = matchesJson.Count,
            ["resultsWindow"] = resultsWindow,
            ["matches"] = matchesJson,
        };
    }

    private async Task PopulateFindResultsAsync(
        IdeCommandContext context,
        IReadOnlyDictionary<string, List<FindResult>> groupedMatches,
        string query,
        int resultsWindow)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);

        var service = await context.Package.GetServiceAsync(typeof(SVsFindResults)).ConfigureAwait(true) as IFindResultsService;
        if (service is null)
        {
            return;
        }

        var title = $"IDE Bridge Find Results {resultsWindow}";
        var description = $"Find all \"{query}\"";
        var identifier = $"VsIdeBridge.FindResults.{resultsWindow}";
        var window = service.StartSearch(title, description, identifier);
        foreach (var item in groupedMatches)
        {
            window.AddResults(item.Key, item.Key, null, item.Value);
        }

        window.Summary = $"Matching lines: {groupedMatches.Sum(item => item.Value.Count)} Matching files: {groupedMatches.Count}";
        window.Complete();
    }

    private static Regex BuildRegex(string query, bool matchCase, bool wholeWord, bool useRegex)
    {
        var pattern = useRegex ? query : Regex.Escape(query);
        if (wholeWord)
        {
            pattern = $@"\b{pattern}\b";
        }

        var options = RegexOptions.Compiled;
        if (!matchCase)
        {
            options |= RegexOptions.IgnoreCase;
        }

        return new Regex(pattern, options);
    }

    private async Task<(string Path, string ProjectUniqueName)> GetDocumentTargetAsync(IdeCommandContext context)
    {
        var activeDocument = context.Dte.ActiveDocument;
        if (activeDocument is null || string.IsNullOrWhiteSpace(activeDocument.FullName))
        {
            throw new CommandErrorException("document_not_found", "There is no active document.");
        }

        await Task.CompletedTask;
        return (activeDocument.FullName, activeDocument.ProjectItem?.ContainingProject?.UniqueName ?? string.Empty);
    }

    private static IEnumerable<(string Path, string ProjectUniqueName)> EnumerateSolutionFiles(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (dte.Solution is null || !dte.Solution.IsOpen)
        {
            yield break;
        }

        foreach (Project project in dte.Solution.Projects)
        {
            foreach (var file in EnumerateProjectFiles(project))
            {
                yield return file;
            }
        }
    }

    private static IEnumerable<(string Path, string ProjectUniqueName)> EnumerateProjectFiles(Project? project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (project is null)
        {
            yield break;
        }

        if (string.Equals(project.Kind, EnvDTE80.ProjectKinds.vsProjectKindSolutionFolder, StringComparison.OrdinalIgnoreCase))
        {
            foreach (ProjectItem item in project.ProjectItems)
            {
                if (item.SubProject is not null)
                {
                    foreach (var file in EnumerateProjectFiles(item.SubProject))
                    {
                        yield return file;
                    }
                }
            }

            yield break;
        }

        foreach (ProjectItem item in project.ProjectItems)
        {
            foreach (var file in EnumerateProjectItemFiles(item, project.UniqueName))
            {
                yield return file;
            }
        }
    }

    private static IEnumerable<(string Path, string ProjectUniqueName)> EnumerateProjectItemFiles(ProjectItem item, string projectUniqueName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (item.FileCount > 0)
        {
            for (short i = 1; i <= item.FileCount; i++)
            {
                var fileName = item.FileNames[i];
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    yield return (fileName, projectUniqueName);
                }
            }
        }

        if (item.ProjectItems is null)
        {
            yield break;
        }

        foreach (ProjectItem child in item.ProjectItems)
        {
            foreach (var file in EnumerateProjectItemFiles(child, projectUniqueName))
            {
                yield return file;
            }
        }
    }
}
