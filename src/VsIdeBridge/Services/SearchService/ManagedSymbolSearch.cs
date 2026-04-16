using EnvDTE80;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Services;

internal sealed partial class SearchService
{
    private static readonly HashSet<string> s_managedSearchExtensions = [".cs", ".vb"];

    private sealed record ManagedSymbolHit
    {
        public string Path { get; set; } = string.Empty;

        public string ProjectUniqueName { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string FullName { get; set; } = string.Empty;

        public string Kind { get; set; } = string.Empty;

        public string Signature { get; set; } = string.Empty;

        public int Line { get; set; }

        public int EndLine { get; set; }

        public int Score { get; set; }

        public string MatchKind { get; set; } = string.Empty;
    }

    private sealed class ManagedSearchOutcome(ManagedSymbolHit[] hits, string stage, string detail)
    {
        public ManagedSymbolHit[] Hits { get; } = hits;

        public string Stage { get; } = stage;

        public string Detail { get; } = detail;
    }

    private async Task<ManagedSearchOutcome> SearchManagedSymbolsAsync(
        IdeCommandContext context,
        string query,
        string kind,
        string scope,
        bool matchCase,
        string? projectUniqueName,
        string? pathFilter,
        int max)
    {
        if (string.IsNullOrWhiteSpace(query) || !SupportsManagedSymbolSearchKind(kind))
        {
            return CreateManagedSearchOutcome([], "skipped", "query_or_kind_not_supported");
        }

        (Dictionary<string, string> pathToProject, IComponentModel? componentModel) = await CaptureManagedSearchContextAsync(
            context,
            scope,
            projectUniqueName,
            pathFilter).ConfigureAwait(true);

        if (componentModel is null || pathToProject.Count == 0)
        {
            return componentModel is null
                ? CreateManagedSearchOutcome([], "component_model_missing", "SComponentModel service not available.")
                : CreateManagedSearchOutcome([], "no_managed_targets", "No managed source files matched the requested scope/path filter.");
        }

        try
        {
            return await SearchManagedSymbolsCoreAsync(
                componentModel,
                pathToProject,
                query,
                kind,
                matchCase,
                max,
                context.CancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            TraceSearchFailure("SearchManagedSymbolsAsync", ex);
            return CreateManagedSearchOutcome([], "search_exception", ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static bool SupportsManagedSymbolSearchKind(string kind)
    {
        return kind.ToLowerInvariant() switch
        {
            "all" => true,
            "type" => true,
            FunctionKind => true,
            "class" => true,
            "struct" => true,
            "enum" => true,
            "namespace" => true,
            InterfaceKind => true,
            "member" => true,
            _ => false,
        };
    }

    private async Task<(Dictionary<string, string> PathToProject, IComponentModel? ComponentModel)> CaptureManagedSearchContextAsync(
        IdeCommandContext context,
        string scope,
        string? projectUniqueName,
        string? pathFilter)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);

        IComponentModel? componentModel = ((System.IServiceProvider)context.Package).GetService(typeof(SComponentModel)) as IComponentModel;
        Dictionary<string, string> pathToProject = ResolveManagedSearchTargets(context.Dte, scope, projectUniqueName, pathFilter);
        return (pathToProject, componentModel);
    }

    private static Dictionary<string, string> ResolveManagedSearchTargets(
        DTE2 dte,
        string scope,
        string? projectUniqueName,
        string? pathFilter)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        string? normalizedPathFilter = NormalizeSearchPathFilter(dte, pathFilter);
        (string Path, string ProjectUniqueName) activeDocument = TryResolveExplicitDocumentTarget(dte, normalizedPathFilter);
        if (string.IsNullOrWhiteSpace(activeDocument.Path))
        {
            activeDocument = TryGetActiveDocumentTarget(dte);
        }

        IEnumerable<(string Path, string ProjectUniqueName)> files = scope switch
        {
            "document" => string.IsNullOrWhiteSpace(activeDocument.Path)
                ? []
                : new[] { activeDocument },
            "open" => EnumerateOpenFiles(dte),
            "project" => EnumerateSolutionFiles(dte)
                .Where(item => MatchesProjectFilter(item.ProjectUniqueName, projectUniqueName)),
            _ => EnumerateSolutionFiles(dte),
        };

        // If project is supplied without scope:"project", honour the intent by applying the
        // project filter on top of whatever scope resolved to (but not document/open — those
        // are intentionally narrow and should not be widened).
        if (!string.IsNullOrWhiteSpace(projectUniqueName)
            && scope != "project" && scope != "document" && scope != "open")
        {
            files = files.Where(item => MatchesProjectFilter(item.ProjectUniqueName, projectUniqueName));
        }

        if (!string.IsNullOrWhiteSpace(normalizedPathFilter))
        {
            files = files.Where(item => MatchesPathFilter(item.Path, normalizedPathFilter));
        }

        // Use OrdinalIgnoreCase so all lookup sites work regardless of path casing.
        Dictionary<string, string> pathToProject = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string Path, string ProjectUniqueName) file in files)
        {
            if (!IsManagedSearchCandidate(file.Path) || pathToProject.ContainsKey(file.Path))
            {
                continue;
            }

            pathToProject[file.Path] = file.ProjectUniqueName;
        }

        return pathToProject;
    }

    private static bool IsManagedSearchCandidate(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string extension = Path.GetExtension(path);
        return s_managedSearchExtensions.Contains(extension.ToLowerInvariant());
    }

    // Matches DTE ProjectUniqueName (e.g. "src\VsIdeBridge\VsIdeBridge.csproj") against a
    // caller-supplied filter that may be a short name ("VsIdeBridge") or the full unique name.
    private static bool MatchesProjectFilter(string projectUniqueName, string? filter)
        => !string.IsNullOrWhiteSpace(filter)
        && (string.Equals(projectUniqueName, filter, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileNameWithoutExtension(projectUniqueName), filter, StringComparison.OrdinalIgnoreCase));

    private static async Task<ManagedSearchOutcome> SearchManagedSymbolsCoreAsync(
        IComponentModel componentModel,
        Dictionary<string, string> pathToProject,
        string query,
        string kind,
        bool matchCase,
        int max,
        CancellationToken cancellationToken)
    {
        VisualStudioWorkspace? workspace = componentModel.GetService<VisualStudioWorkspace>();
        if (workspace is null)
        {
            return CreateManagedSearchOutcome([], "workspace_missing", "VisualStudioWorkspace export was not available.");
        }

        Solution solution = workspace.CurrentSolution;
        SymbolFilter filter = BuildSymbolFilter(kind);
        StringComparison comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        IEnumerable<ISymbol> symbols;
        try
        {
            symbols = await SymbolFinder.FindSourceDeclarationsWithPatternAsync(
                solution, query, filter, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            TraceSearchFailure("SearchManagedSymbolsCoreAsync.FindSourceDeclarations", ex);
            return CreateManagedSearchOutcome([], "search_exception", ex.GetType().Name + ": " + ex.Message);
        }

        List<ManagedSymbolHit> hits = [];
        HashSet<string> seen = [];
        foreach (ISymbol symbol in symbols)
        {
            if (!TryCreateManagedSymbolHit(symbol, pathToProject, query, kind, comparison, out ManagedSymbolHit? hit, out string key))
            {
                continue;
            }

            if (hit is not null && seen.Add(key))
            {
                hits.Add(hit);
            }
        }

        ManagedSymbolHit[] orderedHits = [..hits
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(hit => hit.Line)
            .ThenBy(hit => hit.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, max))];

        return orderedHits.Length > 0
            ? CreateManagedSearchOutcome(orderedHits, "success", string.Empty)
            : CreateManagedSearchOutcome([], "no_hits", "Roslyn search ran but did not return any matching declarations in the requested scope.");
    }

    private static SymbolFilter BuildSymbolFilter(string kind)
    {
        return kind.ToLowerInvariant() switch
        {
            "class" or "struct" or "enum" or InterfaceKind or "type" => SymbolFilter.Type,
            FunctionKind or "member" => SymbolFilter.Member,
            "namespace" => SymbolFilter.Namespace,
            _ => SymbolFilter.All,
        };
    }

    private static ManagedSearchOutcome CreateManagedSearchOutcome(ManagedSymbolHit[] hits, string stage, string detail)
    {
        return new ManagedSearchOutcome(hits, stage, detail);
    }

    private static bool TryCreateManagedSymbolHit(
        ISymbol symbol,
        Dictionary<string, string> pathToProject,
        string query,
        string kindFilter,
        StringComparison comparison,
        out ManagedSymbolHit? hit,
        out string key)
    {
        hit = null;
        key = string.Empty;

        string name = symbol.Name;
        string fullName = symbol.ToDisplayString();
        string normalizedKind = NormalizeManagedSymbolKind(symbol);
        if (!MatchesKind(kindFilter, normalizedKind))
        {
            return false;
        }

        int score = ScoreSymbolMatch(query, name, fullName, comparison, out string matchKind);
        if (score <= 0)
        {
            return false;
        }

        if (!TryGetManagedSymbolLocation(symbol, pathToProject, out string path, out string projectUniqueName, out int line, out int endLine))
        {
            return false;
        }

        key = $"{path}|{normalizedKind}|{fullName}|{line}";
        hit = new ManagedSymbolHit
        {
            Path = path,
            ProjectUniqueName = projectUniqueName,
            Name = name,
            FullName = fullName,
            Kind = normalizedKind,
            Signature = fullName,
            Line = line,
            EndLine = endLine,
            Score = score,
            MatchKind = matchKind,
        };
        return true;
    }

    private static string NormalizeManagedSymbolKind(ISymbol symbol)
    {
        if (symbol.Kind == SymbolKind.Namespace)
        {
            return "namespace";
        }

        if (symbol is INamedTypeSymbol namedType)
        {
            return namedType.TypeKind switch
            {
                TypeKind.Class => "class",
                TypeKind.Struct => "struct",
                TypeKind.Enum => "enum",
                TypeKind.Interface => InterfaceKind,
                _ => "type",
            };
        }

        return symbol.Kind switch
        {
            SymbolKind.Method => FunctionKind,
            SymbolKind.Property => "member",
            SymbolKind.Field => "member",
            SymbolKind.Event => "member",
            _ => "member",
        };
    }

    private static bool TryGetManagedSymbolLocation(
        ISymbol symbol,
        Dictionary<string, string> pathToProject,
        out string path,
        out string projectUniqueName,
        out int line,
        out int endLine)
    {
        path = string.Empty;
        projectUniqueName = string.Empty;
        line = 0;
        endLine = 0;

        foreach (Location location in symbol.Locations)
        {
            if (!location.IsInSource)
            {
                continue;
            }

            try
            {
                FileLinePositionSpan lineSpan = location.GetLineSpan();
                if (string.IsNullOrWhiteSpace(lineSpan.Path))
                {
                    continue;
                }

                string normalizedPath = PathNormalization.NormalizeFilePath(lineSpan.Path);
                if (!pathToProject.TryGetValue(normalizedPath, out string? candidateProject))
                {
                    continue;
                }

                path = normalizedPath;
                projectUniqueName = candidateProject;
                line = lineSpan.StartLinePosition.Line + 1;
                endLine = lineSpan.EndLinePosition.Line + 1;
                return true;
            }
            catch (InvalidOperationException ex)
            {
                TraceSearchFailure("TryGetManagedSymbolLocation", ex);
            }
        }

        return false;
    }

    private static JObject SerializeManagedSymbolHit(ManagedSymbolHit hit)
    {
        return new JObject
        {
            ["name"] = hit.Name,
            ["fullName"] = hit.FullName,
            ["kind"] = hit.Kind,
            ["signature"] = hit.Signature,
            ["path"] = hit.Path,
            ["project"] = hit.ProjectUniqueName,
            ["line"] = hit.Line,
            ["column"] = 1,
            ["endLine"] = hit.EndLine,
            ["matchKind"] = hit.MatchKind,
            ["scoreHint"] = hit.Score,
            ["preview"] = hit.Signature,
            ["source"] = "roslyn",
        };
    }

    private static JObject SerializeManagedSearchOutcome(ManagedSearchOutcome outcome)
    {
        return new JObject
        {
            ["attempted"] = true,
            ["stage"] = outcome.Stage,
            ["detail"] = outcome.Detail,
            ["hitCount"] = outcome.Hits.Length,
        };
    }
}
