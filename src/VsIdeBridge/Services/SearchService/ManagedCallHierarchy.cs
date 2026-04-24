using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;

namespace VsIdeBridge.Services;

internal sealed partial class SearchService
{
    private sealed class ManagedCallHierarchyOutcome(JObject sourceLocation, JObject? root, int nodeCount, string stage, string detail)
    {
        public JObject SourceLocation { get; } = sourceLocation;

        public JObject? Root { get; } = root;

        public int NodeCount { get; } = nodeCount;

        public string Stage { get; } = stage;

        public string Detail { get; } = detail;
    }

    public async Task<JObject> GetCallHierarchyAsync(
        IdeCommandContext context,
        string? filePath,
        string? documentQuery,
        int? line,
        int? column,
        int maxDepth,
        int maxChildrenPerNode,
        int maxLocationsPerCaller)
    {
        ManagedCallHierarchyOutcome outcome = await GetManagedCallHierarchyAsync(
            context,
            filePath,
            documentQuery,
            line,
            column,
            maxDepth,
            maxChildrenPerNode,
            maxLocationsPerCaller).ConfigureAwait(true);

        return SerializeManagedCallHierarchyOutcome(outcome);
    }

    private async Task<ManagedCallHierarchyOutcome> GetManagedCallHierarchyAsync(
        IdeCommandContext context,
        string? filePath,
        string? documentQuery,
        int? line,
        int? column,
        int maxDepth,
        int maxChildrenPerNode,
        int maxLocationsPerCaller)
    {
        JObject sourceLocation = await context.Runtime.DocumentService
            .PositionTextSelectionAsync(context.Dte, filePath, documentQuery, line, column, selectWord: true)
            .ConfigureAwait(true);

        string selectedText = ((string?)sourceLocation["selectedText"] ?? string.Empty).Trim();
        sourceLocation["selectedText"] = selectedText;

        string sourcePath = (string?)sourceLocation["resolvedPath"] ?? string.Empty;
        int sourceLine = (int?)sourceLocation["line"] ?? 0;
        int sourceColumn = (int?)sourceLocation["column"] ?? 1;
        string sourceLineText = (string?)sourceLocation["lineText"] ?? string.Empty;
        string resolvedSymbol = ResolveCallHierarchySymbolCandidate(selectedText, sourceLineText, sourceColumn);
        sourceLocation["resolvedSymbol"] = resolvedSymbol;

        if (!IsManagedSearchCandidate(sourcePath))
        {
            return CreateManagedCallHierarchyOutcome(
                sourceLocation,
                null,
                0,
                "unsupported_language",
                "Managed call hierarchy currently supports C# and Visual Basic documents.");
        }

        if (string.IsNullOrWhiteSpace(resolvedSymbol))
        {
            return CreateManagedCallHierarchyOutcome(
                sourceLocation,
                null,
                0,
                "no_symbol",
                "No navigable symbol was found under the caret.");
        }

        // Fetch component model and workspace on the UI thread - both are fast service lookups.
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);
        if (((System.IServiceProvider)context.Package).GetService(typeof(Microsoft.VisualStudio.ComponentModelHost.SComponentModel))
            is not Microsoft.VisualStudio.ComponentModelHost.IComponentModel componentModel)
        {
            return CreateManagedCallHierarchyOutcome(sourceLocation, null, 0, "component_model_missing", "SComponentModel service not available.");
        }

        VisualStudioWorkspace? workspace = TryGetVisualStudioWorkspace(componentModel);
        if (workspace is null)
        {
            return CreateManagedCallHierarchyOutcome(sourceLocation, null, 0, "workspace_missing", "VisualStudioWorkspace export was not available.");
        }

        // Capture the solution snapshot (immutable) then build pathToProject on a thread-pool
        // thread from Roslyn data - no DTE COM enumeration, no UI-thread blocking.
        Solution solution = workspace.CurrentSolution;
        Dictionary<string, string> pathToProject = await Task.Run(
            () => BuildPathToProjectFromSolution(solution), context.CancellationToken).ConfigureAwait(false);

        if (pathToProject.Count == 0 || !pathToProject.ContainsKey(sourcePath))
        {
            return CreateManagedCallHierarchyOutcome(
                sourceLocation,
                null,
                0,
                "no_managed_targets",
                "The current source file was not available in the managed workspace scope.");
        }

        ISymbol? targetSymbol = await ResolveManagedTargetSymbolAsync(
            solution,
            pathToProject,
            resolvedSymbol,
            sourcePath,
            sourceLine,
            sourceColumn,
            context.CancellationToken).ConfigureAwait(false);

        if (targetSymbol is null)
        {
            return CreateManagedCallHierarchyOutcome(
                sourceLocation,
                null,
                0,
                "target_symbol_not_resolved",
                "Managed search could not resolve the selected declaration from the current file/line.");
        }

        HashSet<string> visited = [];
        (JObject? root, int nodeCount) = await BuildManagedCallHierarchyNodeAsync(
            targetSymbol,
            solution,
            pathToProject,
            Math.Max(0, maxDepth),
            Math.Max(1, maxChildrenPerNode),
            Math.Max(1, maxLocationsPerCaller),
            visited,
            context.CancellationToken).ConfigureAwait(false);

        return root is not null
            ? CreateManagedCallHierarchyOutcome(sourceLocation, root, nodeCount, "success", string.Empty)
            : CreateManagedCallHierarchyOutcome(sourceLocation, null, 0, "no_hierarchy", "Managed caller search did not return any hierarchy data.");
    }

    private static ManagedCallHierarchyOutcome CreateManagedCallHierarchyOutcome(
        JObject sourceLocation,
        JObject? root,
        int nodeCount,
        string stage,
        string detail)
    {
        return new ManagedCallHierarchyOutcome(sourceLocation, root, nodeCount, stage, detail);
    }

    private static JObject SerializeManagedCallHierarchyOutcome(ManagedCallHierarchyOutcome outcome)
    {
        return new JObject
        {
            ["attempted"] = true,
            ["available"] = outcome.Root is not null,
            ["stage"] = outcome.Stage,
            ["detail"] = outcome.Detail,
            ["nodeCount"] = outcome.NodeCount,
            ["sourceLocation"] = outcome.SourceLocation,
            ["root"] = outcome.Root,
        };
    }

    private static async Task<ISymbol?> ResolveManagedTargetSymbolAsync(
        Solution solution,
        Dictionary<string, string> pathToProject,
        string selectedText,
        string sourcePath,
        int sourceLine,
        int sourceColumn,
        CancellationToken cancellationToken)
    {
        ISymbol? documentResolvedSymbol = await ResolveManagedTargetSymbolFromDocumentAsync(
            solution,
            sourcePath,
            sourceLine,
            sourceColumn,
            cancellationToken).ConfigureAwait(false);
        if (documentResolvedSymbol is not null)
        {
            return documentResolvedSymbol;
        }

        IEnumerable<ISymbol> symbols = await SymbolFinder.FindSourceDeclarationsWithPatternAsync(
            solution,
            selectedText,
            SymbolFilter.All,
            cancellationToken).ConfigureAwait(false);

        ISymbol? bestSymbol = null;
        int bestScore = int.MinValue;
        foreach (ISymbol symbol in symbols)
        {
            if (!TryCreateManagedSymbolDescriptor(symbol, pathToProject, out ManagedSymbolHit? hit, out _))
            {
                continue;
            }

            if (hit is null)
            {
                continue;
            }

            if (!string.Equals(hit.Name, selectedText, StringComparison.Ordinal))
            {
                continue;
            }

            int score = ScoreManagedTargetCandidate(hit, sourcePath, sourceLine, sourceColumn);
            if (score > bestScore)
            {
                bestScore = score;
                bestSymbol = symbol;
            }
        }

        return bestSymbol;
    }

    private static async Task<ISymbol?> ResolveManagedTargetSymbolFromDocumentAsync(
        Solution solution,
        string sourcePath,
        int sourceLine,
        int sourceColumn,
        CancellationToken cancellationToken)
    {
        Document? document = TryResolveManagedDocument(solution, sourcePath);
        if (document is null)
        {
            return null;
        }

        SourceText? sourceText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        SyntaxNode? syntaxRoot = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        SemanticModel? semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (sourceText is null || syntaxRoot is null || semanticModel is null)
        {
            return null;
        }

        int? position = TryGetSourcePosition(sourceText, sourceLine, sourceColumn);
        if (!position.HasValue)
        {
            return null;
        }

        return ResolveManagedSymbolAtPosition(semanticModel, syntaxRoot, position.Value, cancellationToken);
    }

    private static Document? TryResolveManagedDocument(Solution solution, string sourcePath)
    {
        string normalizedSourcePath = PathNormalization.NormalizeFilePath(sourcePath);

        foreach (DocumentId documentId in solution.GetDocumentIdsWithFilePath(normalizedSourcePath))
        {
            Document? document = solution.GetDocument(documentId);
            if (document is not null && DocumentMatchesPath(document, normalizedSourcePath))
            {
                return document;
            }
        }

        foreach (Project project in solution.Projects)
        {
            foreach (Document document in project.Documents)
            {
                if (DocumentMatchesPath(document, normalizedSourcePath))
                {
                    return document;
                }
            }
        }

        return null;
    }

    private static bool DocumentMatchesPath(Document document, string normalizedSourcePath)
    {
        string documentPath = PathNormalization.NormalizeFilePath(document.FilePath ?? string.Empty);
        return !string.IsNullOrWhiteSpace(documentPath)
            && string.Equals(documentPath, normalizedSourcePath, StringComparison.OrdinalIgnoreCase);
    }

    private static int? TryGetSourcePosition(SourceText sourceText, int sourceLine, int sourceColumn)
    {
        TextLineCollection lines = sourceText.Lines;
        int lineIndex = Math.Max(0, sourceLine - 1);
        if (lineIndex >= lines.Count)
        {
            return null;
        }

        TextLine textLine = lines[lineIndex];
        int zeroBasedColumnOffset = Math.Max(0, sourceColumn - 1);
        return Math.Min(textLine.End, textLine.Start + zeroBasedColumnOffset);
    }

    private static ISymbol? ResolveManagedSymbolAtPosition(
        SemanticModel semanticModel,
        SyntaxNode syntaxRoot,
        int position,
        CancellationToken cancellationToken)
    {
        SyntaxToken token = syntaxRoot.FindToken(position);
        List<SyntaxNode> candidateNodes = CollectCandidateSyntaxNodes(token);
        foreach (SyntaxNode node in candidateNodes)
        {
            ISymbol? declaredSymbol = semanticModel.GetDeclaredSymbol(node, cancellationToken);
            if (declaredSymbol is not null)
            {
                return declaredSymbol;
            }

            ISymbol? referencedSymbol = semanticModel.GetSymbolInfo(node, cancellationToken).Symbol;
            if (referencedSymbol is not null)
            {
                return referencedSymbol;
            }
        }

        return null;
    }

    private static List<SyntaxNode> CollectCandidateSyntaxNodes(SyntaxToken token)
    {
        List<SyntaxNode> nodes = [];
        SyntaxNode? current = token.Parent;
        while (current is not null)
        {
            nodes.Add(current);
            current = current.Parent;
        }

        return nodes;
    }

    private static int ScoreManagedTargetCandidate(ManagedSymbolHit hit, string sourcePath, int sourceLine, int sourceColumn)
    {
        if (!string.Equals(hit.Path, sourcePath, StringComparison.OrdinalIgnoreCase))
        {
            return int.MinValue;
        }

        int score = 1000;
        if (sourceLine >= hit.Line && (hit.EndLine <= 0 || sourceLine <= hit.EndLine))
        {
            score += 500;
        }
        else
        {
            score -= Math.Abs(hit.Line - sourceLine) * 10;
        }

        score -= Math.Max(0, sourceColumn - 1);
        return score;
    }

    private static string ResolveCallHierarchySymbolCandidate(string selectedText, string lineText, int sourceColumn)
    {
        if (HasNavigableIdentifierText(selectedText))
        {
            return selectedText;
        }

        return TryResolveSymbolFromLine(lineText, sourceColumn);
    }

    private static string TryResolveSymbolFromLine(string lineText, int sourceColumn)
    {
        if (string.IsNullOrWhiteSpace(lineText))
        {
            return string.Empty;
        }

        Match[] matches = [..Regex.Matches(lineText, @"\b[_\p{L}][_\p{L}\p{Nd}]*\b")
            .Cast<Match>()
            .Where(match => HasNavigableIdentifierText(match.Value))
            ];

        if (matches.Length == 0)
        {
            return string.Empty;
        }

        int zeroBasedColumn = Math.Max(0, sourceColumn - 1);
        Match? containing = matches.FirstOrDefault(match => zeroBasedColumn >= match.Index && zeroBasedColumn < match.Index + match.Length);
        if (containing is not null)
        {
            return containing.Value;
        }

        Match? next = matches.FirstOrDefault(match => match.Index >= zeroBasedColumn);
        if (next is not null)
        {
            return next.Value;
        }

        return matches[matches.Length - 1].Value;
    }

    private static bool HasNavigableIdentifierText(string text)
    {
        return !string.IsNullOrWhiteSpace(text)
            && text.Any(static character => char.IsLetterOrDigit(character) || character == '_');
    }

    private static async Task<(JObject? Root, int NodeCount)> BuildManagedCallHierarchyNodeAsync(
        ISymbol symbol,
        Solution solution,
        Dictionary<string, string> pathToProject,
        int depthRemaining,
        int maxChildrenPerNode,
        int maxLocationsPerCaller,
        HashSet<string> visited,
        CancellationToken cancellationToken)
    {
        if (!TryCreateManagedSymbolDescriptor(symbol, pathToProject, out ManagedSymbolHit? hit, out string key))
        {
            return (null, 0);
        }

        if (!visited.Add(key))
        {
            JObject cycleNode = SerializeManagedSymbolHit(hit!);
            cycleNode["callers"] = new JArray();
            cycleNode["callSites"] = new JArray();
            cycleNode["callSiteCount"] = 0;
            cycleNode["callerCount"] = 0;
            cycleNode["truncatedReason"] = "cycle_detected";
            return (cycleNode, 1);
        }

        JObject node = SerializeManagedSymbolHit(hit!);
        JArray callers = [];
        int totalNodes = 1;

        if (depthRemaining > 0)
        {
            IEnumerable<SymbolCallerInfo> callerInfos = await FindManagedCallersAsync(symbol, solution, cancellationToken).ConfigureAwait(false);
            foreach (SymbolCallerInfo callerInfo in callerInfos.Take(maxChildrenPerNode))
            {
                (JObject? Child, int ChildNodeCount) = await BuildManagedCallHierarchyNodeAsync(
                    callerInfo.CallingSymbol,
                    solution,
                    pathToProject,
                    depthRemaining - 1,
                    maxChildrenPerNode,
                    maxLocationsPerCaller,
                    visited,
                    cancellationToken).ConfigureAwait(false);
                if (Child is null)
                {
                    continue;
                }

                JArray callSites = SerializeManagedCallerLocations(callerInfo, pathToProject, maxLocationsPerCaller);
                Child["isDirect"] = callerInfo.IsDirect;
                Child["callSites"] = callSites;
                Child["callSiteCount"] = callSites.Count;
                callers.Add(Child);
                totalNodes += ChildNodeCount;
            }
        }

        node["callers"] = callers;
        node["callerCount"] = callers.Count;
        node["callSites"] = new JArray();
        node["callSiteCount"] = 0;
        return (node, totalNodes);
    }

    private static async Task<IEnumerable<SymbolCallerInfo>> FindManagedCallersAsync(
        ISymbol symbol,
        Solution solution,
        CancellationToken cancellationToken)
    {
        return await SymbolFinder.FindCallersAsync(symbol, solution, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryCreateManagedSymbolDescriptor(
        ISymbol symbol,
        Dictionary<string, string> pathToProject,
        out ManagedSymbolHit? hit,
        out string key)
    {
        hit = null;
        key = string.Empty;

        string name = symbol.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (!TryGetManagedSymbolLocation(symbol, pathToProject, out string path, out string projectUniqueName, out int line, out int endLine))
        {
            return false;
        }

        string fullName = symbol.ToDisplayString();
        string normalizedKind = NormalizeManagedSymbolKind(symbol);
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
            Score = 0,
            MatchKind = string.Empty,
        };
        return true;
    }

    private static JArray SerializeManagedCallerLocations(
        SymbolCallerInfo callerInfo,
        Dictionary<string, string> pathToProject,
        int maxLocationsPerCaller)
    {
        JArray results = [];
        foreach (Location location in callerInfo.Locations.Take(maxLocationsPerCaller))
        {
            if (!TryGetManagedLocationFromLocation(location, pathToProject, out string path, out string projectUniqueName, out int line, out int endLine))
            {
                continue;
            }

            results.Add(new JObject
            {
                ["path"] = path,
                ["project"] = projectUniqueName,
                ["line"] = line,
                ["column"] = 1,
                ["endLine"] = endLine,
                ["source"] = "roslyn",
            });
        }

        return results;
    }

    private static bool TryGetManagedLocationFromLocation(
        Location location,
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

        if (!location.IsInSource)
        {
            return false;
        }

        try
        {
            FileLinePositionSpan lineSpan = location.GetLineSpan();
            if (string.IsNullOrWhiteSpace(lineSpan.Path))
            {
                return false;
            }

            string normalizedPath = PathNormalization.NormalizeFilePath(lineSpan.Path);
            if (!pathToProject.TryGetValue(normalizedPath, out string? candidateProject))
            {
                return false;
            }

            path = normalizedPath;
            projectUniqueName = candidateProject;
            line = lineSpan.StartLinePosition.Line + 1;
            endLine = lineSpan.EndLinePosition.Line + 1;
            return true;
        }
        catch (InvalidOperationException ex)
        {
            TraceSearchFailure("TryGetManagedLocationFromLocation", ex);
            return false;
        }
    }

    private static Dictionary<string, string> BuildPathToProjectFromSolution(Solution solution)
    {
        return solution.Projects
            .SelectMany(project => project.Documents.Select(document => new
            {
                projectName = project.FilePath ?? project.Name,
                filePath = document.FilePath
            }))
            .Where(item => item.filePath is not null && IsManagedSearchCandidate(item.filePath))
            .Select(item => new
            {
                item.projectName,
                normalizedPath = PathNormalization.NormalizeFilePath(item.filePath!)
            })
            .GroupBy(item => item.normalizedPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().projectName, StringComparer.OrdinalIgnoreCase);
    }

    private static VisualStudioWorkspace? TryGetVisualStudioWorkspace(Microsoft.VisualStudio.ComponentModelHost.IComponentModel? componentModel)
    {
        return componentModel?.GetService<VisualStudioWorkspace>();
    }
}
