using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.TableManager;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Diagnostics;
using static VsIdeBridge.Diagnostics.ErrorListConstants;

namespace VsIdeBridge.Services;

internal sealed partial class ErrorListService(VsIdeBridgePackage package, ReadinessService readinessService, BridgeUiSettingsService uiSettings)
{
    private readonly ErrorListProvider _bestPracticeProvider = new(package)
    {
        ProviderName = "VS IDE Bridge Best Practices",
    };
    private readonly VsIdeBridgePackage _package = package;
    private BestPracticeTableDataSource? _bestPracticeTableSource;
    private bool _bestPracticeTableSourceRegistered;
    private readonly ReadinessService _readinessService = readinessService;
    private readonly BridgeUiSettingsService _uiSettings = uiSettings;

    public async Task<JObject> GetErrorListAsync(
        IdeCommandContext context,
        bool waitForIntellisense,
        int timeoutMilliseconds,
        bool quickSnapshot = false,
        ErrorListQuery? query = null,
        bool includeBuildOutputFallback = false,
        bool afterEdit = false,
        bool forceRefresh = false)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);

        if (!quickSnapshot)
        {
            PublishBestPracticeRows(context.Dte, []);
        }

        // Readiness is allowed to delay a passive snapshot, but it must not force the
        // active refresh path that can clear and repopulate the Error List in large C++ solutions.
        if (waitForIntellisense)
        {
            await _readinessService.WaitForReadyAsync(context, timeoutMilliseconds, afterEdit).ConfigureAwait(true);
        }

        IReadOnlyList<JObject> rows;
        if (quickSnapshot)
        {
            EnsureErrorListWindow(context.Dte);
            // Use the synchronous table read: subscribes and reads whatever is
            // currently cached by each provider immediately, without holding the
            // subscription open and waiting for WaitForStabilityAsync. That wait
            // loop is what caused C++ projects to time out — normal IntelliSense
            // background updates kept resetting the stability counter forever.
            if (!TryReadTableRows(out rows) || rows.Count == 0)
            {
                try
                {
                    rows = await ReadDteRowsAsync(context, rows).ConfigureAwait(true);
                }
                catch (InvalidOperationException ex)
                {
                    LogNonCriticalException(ex);
                }
            }
        }
        else
        {
            rows = await WaitForRowsAsync(context, timeoutMilliseconds, forceRefresh).ConfigureAwait(true);
        }

        if (includeBuildOutputFallback)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);
            IReadOnlyList<JObject> buildOutputRows = ReadBuildOutputRows(context.Dte);
            if (rows.Count == 0)
            {
                rows = buildOutputRows;
            }
        }

        if (!includeBuildOutputFallback)
        {
            rows = ExcludeBuildOutputRows(rows);
        }

        if (!quickSnapshot)
        {
            IReadOnlyList<JObject> bestPracticeRows = await RefreshBestPracticeDiagnosticsAsync(context, rows).ConfigureAwait(true);
            if (bestPracticeRows.Count > 0)
            {
                rows = MergeRows(rows, bestPracticeRows);
            }
        }

        JObject[] matchingRows = [.. ApplyQuery(rows, query?.WithoutMax())];
        JObject[] filteredRows = query?.Max > 0
            ? [.. matchingRows.Take(query.Max.Value)]
            : matchingRows;
        Dictionary<string, int> severityCounts = CreateSeverityCounts();
        foreach (JObject row in filteredRows)
        {
            severityCounts[(string)row[SeverityKey]!]++;
        }

        Dictionary<string, int> totalSeverityCounts = CreateSeverityCounts();
        foreach (JObject row in rows)
        {
            totalSeverityCounts[(string)row[SeverityKey]!]++;
        }

        return new JObject
        {
            ["count"] = filteredRows.Length,
            ["totalCount"] = matchingRows.Length,
            ["severityCounts"] = JObject.FromObject(severityCounts),
            ["totalSeverityCounts"] = JObject.FromObject(totalSeverityCounts),
            ["hasErrors"] = severityCounts["Error"] > 0,
            ["hasWarnings"] = severityCounts["Warning"] > 0,
            ["filter"] = query?.ToJson() ?? [],
            ["rows"] = new JArray(filteredRows),
            ["groups"] = BuildGroups(matchingRows, query?.GroupBy),
        };
    }

    internal async Task<IReadOnlyList<JObject>> RefreshBestPracticeDiagnosticsAsync(IdeCommandContext context, IReadOnlyList<JObject>? rows = null)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);
        if (!_uiSettings.BestPracticeDiagnosticsEnabled)
        {
            PublishBestPracticeRows(context.Dte, []);
            return [];
        }

        IReadOnlyList<string> bestPracticeCandidateFiles = GetBestPracticeCandidateFiles(context.Dte, rows ?? []);
        IReadOnlyDictionary<string, string> bestPracticeProjectLookup = CreateBestPracticeProjectLookup(context.Dte, bestPracticeCandidateFiles);
        IReadOnlyList<JObject> bestPracticeRows = await Task.Run(
            () => AnalyzeBestPracticeFindings(bestPracticeCandidateFiles, bestPracticeProjectLookup),
            context.CancellationToken).ConfigureAwait(false);
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);
        PublishBestPracticeRows(context.Dte, bestPracticeRows);
        return bestPracticeRows;
    }

    private static IReadOnlyDictionary<string, string> CreateBestPracticeProjectLookup(DTE2 dte, IReadOnlyList<string> files)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        Dictionary<string, string> projectNamesByFile = [];
        foreach (string file in files)
        {
            string? projectUniqueName = SolutionFileLocator.TryFindProjectUniqueName(dte, file);
            if (!string.IsNullOrWhiteSpace(projectUniqueName))
            {
                projectNamesByFile[file.ToLowerInvariant()] = projectUniqueName!;
            }
        }

        return projectNamesByFile;
    }

    private static string TryGetBestPracticeProjectUniqueName(IReadOnlyDictionary<string, string>? projectNamesByFile, string file)
    {
        if (projectNamesByFile is null)
        {
            return string.Empty;
        }

        return projectNamesByFile.TryGetValue(file.ToLowerInvariant(), out string? projectUniqueName)
            ? projectUniqueName ?? string.Empty
            : string.Empty;
    }

    private static IReadOnlyList<string> GetBestPracticeCandidateFiles(DTE2 dte, IReadOnlyList<JObject> rows)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        HashSet<string> seen = [];
        List<string> files = [];

        // First add files from error rows (if any).
        foreach (var path in rows
            .Select(row => row["file"]?.ToString())
            .OfType<string>()
            .Where(IsBestPracticeCandidateFile))
        {
            if (seen.Add(path.ToLowerInvariant()))
            {
                files.Add(path);
            }
        }

        // Then enumerate all solution project files.
        foreach (var (path, _) in SolutionFileLocator.EnumerateSolutionFiles(dte))
        {
            if (files.Count >= MaxBestPracticeFiles)
            {
                break;
            }

            if (!IsBestPracticeCandidateFile(path))
            {
                continue;
            }

            if (seen.Add(path.ToLowerInvariant()))
            {
                files.Add(path);
            }
        }

        foreach (string path in EnumerateRepoBestPracticeFiles(dte))
        {
            if (files.Count >= MaxBestPracticeFiles)
            {
                break;
            }

            if (seen.Add(path.ToLowerInvariant()))
            {
                files.Add(path);
            }
        }

        return files;
    }

    private static IEnumerable<string> EnumerateRepoBestPracticeFiles(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        string solutionFullName = dte.Solution?.FullName ?? string.Empty;
        string? solutionRoot = string.IsNullOrWhiteSpace(solutionFullName)
            ? null
            : Path.GetDirectoryName(solutionFullName);

        if (string.IsNullOrWhiteSpace(solutionRoot))
        {
            yield break;
        }

        string scriptsDirectory = Path.Combine(solutionRoot, "scripts");
        if (!Directory.Exists(scriptsDirectory))
        {
            yield break;
        }

        foreach (string path in Directory.EnumerateFiles(scriptsDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            if (IsBestPracticeCandidateFile(path))
            {
                yield return path;
            }
        }
    }

    private static bool IsBestPracticeCandidateFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        string fullPath = Path.GetFullPath(path);
        foreach (var fragment in IgnoredBestPracticePathFragments)
        {
            if (fullPath.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }
        }

        string fileName = Path.GetFileName(fullPath);
        if (fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string extension = Path.GetExtension(fullPath);
        return !string.IsNullOrWhiteSpace(extension) && BestPracticeCodeExtensions.Contains(extension);
    }

    private static IReadOnlyList<JObject> AnalyzeBestPracticeFindings(
        IReadOnlyList<string> files,
        IReadOnlyDictionary<string, string>? projectNamesByFile = null,
        string? contentOverride = null)
    {
        List<JObject> findings = [];

        foreach (string file in files)
        {
            string content = contentOverride ?? SafeReadFile(file);
            int perFileFindings = 0;
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            string projectUniqueName = TryGetBestPracticeProjectUniqueName(projectNamesByFile, file);
            IEnumerable<JObject> fileFindings = BestPracticeAnalyzer.AnalyzeFile(file, content);

            foreach (JObject finding in fileFindings)
            {
                if (!string.IsNullOrWhiteSpace(projectUniqueName))
                {
                    finding[ProjectKey] = projectUniqueName;
                }

                findings.Add(finding);
                perFileFindings++;
                if (perFileFindings >= MaxBestPracticeFindingsPerFile)
                {
                    break;
                }
            }
        }

        return [.. findings
            .GroupBy(CreateFindingIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())];
    }

    /// <summary>
    /// Pre-write analysis: scans content that is about to be written and returns best-practice
    /// warnings without publishing them to the Error List. Callers (PatchService, write-file)
    /// can include these in their response so the LLM sees issues immediately.
    /// </summary>
    internal static IReadOnlyList<JObject> AnalyzeContentBeforeWrite(string filePath, string content)
    {
        if (string.IsNullOrWhiteSpace(content) || !IsBestPracticeCandidateFile(filePath))
        {
            return [];
        }

        return AnalyzeBestPracticeFindings([filePath], contentOverride: content);
    }

    private static string SafeReadFile(string filePath)
    {
        try
        {
            return File.ReadAllText(filePath);
        }
        catch
        {
            return string.Empty;
        }
    }

}
