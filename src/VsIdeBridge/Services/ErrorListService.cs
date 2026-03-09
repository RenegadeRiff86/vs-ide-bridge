using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.ComponentModelHost;
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

namespace VsIdeBridge.Services;

internal sealed class ErrorListQuery
{
    public string? Severity { get; set; }

    public string? Code { get; set; }

    public string? Project { get; set; }

    public string? Path { get; set; }

    public string? Text { get; set; }

    public string? GroupBy { get; set; }

    public int? Max { get; set; }

    public JObject ToJson()
    {
        return new JObject
        {
            ["severity"] = Severity ?? string.Empty,
            ["code"] = Code ?? string.Empty,
            ["project"] = Project ?? string.Empty,
            ["path"] = Path ?? string.Empty,
            ["text"] = Text ?? string.Empty,
            ["groupBy"] = GroupBy ?? string.Empty,
            ["max"] = (JToken?)Max ?? JValue.CreateNull(),
        };
    }
}

internal sealed class ErrorListService(VsIdeBridgePackage package, ReadinessService readinessService, BridgeUiSettingsService uiSettings)
{
    private const int StableSampleCount = 3;
    private const int PopulationPollIntervalMilliseconds = 2000;
    private const int DefaultWaitTimeoutMilliseconds = 90_000;
    private const int MaxBestPracticeFiles = 64;
    private const int MaxBestPracticeFindingsPerFile = 25;
    private const int RepeatedStringThreshold = 5;
    private const int RepeatedNumberThreshold = 4;
    private const int MaxSuppressionFindingsPerFile = 5;

    private const int LinkerCodePrefixLength = 4;
    private const string SeverityKey = "severity";
    private const string CodeKey = "code";
    private const string ProjectKey = "project";
    private const string FileKey = "file";
    private const string LineKey = "line";
    private const string ColumnKey = "column";
    private const string MessageKey = "message";
    private const string ToolKey = "tool";
    private const string CodeFamilyKey = "codeFamily";
    private const string SymbolsKey = "symbols";
    private const string SourceKey = "source";
    private const string HelpUriKey = "helpUri";
    private const string WarningSeverity = "Warning";
    private const string BestPracticeCategory = "best-practice";

    // Documentation URIs surfaced in MCP error output so AI assistants can link to the relevant guidance.
    private const string BP1001HelpUri = "https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions";
    private const string BP1002HelpUri = "https://learn.microsoft.com/en-us/dotnet/standard/design-guidelines/general-naming-conventions";
    private const string BP1003HelpUri = "https://learn.microsoft.com/en-us/dotnet/standard/exceptions/best-practices-for-exceptions";
    private const string BP1004HelpUri = "https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/quality-rules/ca1031";
    private const string BP1005HelpUri = "https://learn.microsoft.com/en-us/archive/msdn-magazine/2013/march/async-await-best-practices-in-asynchronous-programming";
    private const string BP1006HelpUri = "https://isocpp.github.io/CppCoreGuidelines/CppCoreGuidelines#r11-avoid-calling-new-and-delete-explicitly";
    private const string BP1007HelpUri = "https://isocpp.github.io/CppCoreGuidelines/CppCoreGuidelines#sf7-dont-write-using-namespace-at-global-scope-in-a-header-file";
    private const string BP1008HelpUri = "https://isocpp.github.io/CppCoreGuidelines/CppCoreGuidelines#es49-if-you-must-use-a-cast-use-a-named-cast";
    private const string BP1009HelpUri = "https://docs.python.org/3/howto/logging.html#exceptions";
    private const string BP1010HelpUri = "https://docs.python.org/3/reference/compound_stmts.html#function-definitions";
    private const string BP1011HelpUri = "https://peps.python.org/pep-0008/#imports";

    private static readonly HashSet<string> BestPracticeCodeExtensions = new(StringComparer.OrdinalIgnoreCase) { ".cs", ".vb", ".fs", ".c", ".cc", ".cpp", ".cxx", ".h", ".hh", ".hpp", ".hxx", ".py" };
    private static readonly string[] IgnoredBestPracticePathFragments = ["\\.vs\\", "\\bin\\", "\\obj\\", "\\output\\"];
    private static readonly string[] BuildOutputPaneNames = ["Build", "Build Order"];
    private static readonly string[] BestPracticeTableColumns =
    [
        StandardTableKeyNames.ErrorSeverity,
        StandardTableKeyNames.ErrorCode,
        StandardTableKeyNames.ErrorCodeToolTip,
        StandardTableKeyNames.Text,
        StandardTableKeyNames.DocumentName,
        StandardTableKeyNames.Path,
        StandardTableKeyNames.Line,
        StandardTableKeyNames.Column,
        StandardTableKeyNames.ProjectName,
        StandardTableKeyNames.BuildTool,
        StandardTableKeyNames.ErrorSource,
        StandardTableKeyNames.HelpKeyword,
        StandardTableKeyNames.HelpLink,
        StandardTableKeyNames.FullText,
    ];
    private static readonly Regex ExplicitCodePattern = new(
        @"\b(?:LINK|LNK|MSB|VCR|E|C)\d+\b|\blnt-[a-z0-9-]+\b|\bInt-make\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MsBuildDiagnosticPattern = new(
        @"^(?<file>[A-Za-z]:\\.*?|\S.*?)(?:\((?<line>\d+)(?:,(?<column>\d+))?\))?\s*:\s*(?<severity>warning|error)\s+(?<code>[A-Za-z]+[A-Za-z0-9-]*)\s*:\s*(?<message>.+?)(?:\s+\[(?<project>[^\]]+)\])?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex StructuredOutputPattern = new(
        @"^(?<project>.+?)\s*>\s*(?<file>[A-Za-z]:\\.*?|\S.*?)(?:\((?<line>\d+)(?:,(?<column>\d+))?\))?\s*:\s*(?<severity>warning|error)\s+(?<code>[A-Za-z]+[A-Za-z0-9-]*)\s*:\s*(?<message>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex StringLiteralPattern = new("\"([^\"\\r\\n]{8,})\"", RegexOptions.Compiled);
    private static readonly Regex NumberLiteralPattern = new(@"(?<![A-Za-z0-9_\.])(?<value>-?\d+(?:\.\d+)?)\b", RegexOptions.Compiled);
    private static readonly Regex SuspiciousRoundDownPattern = new(@"Math\s*\.\s*(?<op>Floor|Truncate)\s*\(", RegexOptions.Compiled);
    private static readonly Regex EmptyCatchBlockPattern = new(@"catch\s*(?:\([^)]*\))?\s*\{\s*\}", RegexOptions.Compiled);
    private static readonly Regex AsyncVoidPattern = new(@"\basync\s+void\s+(\w+)", RegexOptions.Compiled);
    private static readonly Regex RawDeletePattern = new(@"\bdelete\s*(\[\])?\s", RegexOptions.Compiled);
    private static readonly Regex UsingNamespacePattern = new(@"^\s*using\s+namespace\s+([\w:]+)\s*;", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex CStyleCastPattern = new(@"\((?:const\s+)?(?:unsigned\s+)?(?:int|long|short|char|float|double|size_t|uint\d+_t|int\d+_t|void)\s*\*?\)\s*[a-zA-Z_\(]", RegexOptions.Compiled);
    private static readonly Regex BareExceptPattern = new(@"^\s*except\s*:", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex MutableDefaultArgPattern = new(@"\bdef\s+(\w+)\s*\([^)]*=\s*(?:\[\s*\]|\{\s*\}|set\s*\(\s*\))", RegexOptions.Compiled);
    private static readonly Regex ImportStarPattern = new(@"^\s*from\s+(\S+)\s+import\s+\*", RegexOptions.Compiled | RegexOptions.Multiline);

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
        ErrorListQuery? query = null)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);

        if (!quickSnapshot)
        {
            PublishBestPracticeRows(context.Dte, []);
        }

        if (waitForIntellisense && !quickSnapshot)
        {
            await _readinessService.WaitForReadyAsync(context, timeoutMilliseconds).ConfigureAwait(true);
        }

        IReadOnlyList<JObject> rows;
        if (quickSnapshot)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);
            EnsureErrorListWindow(context.Dte);
            try
            {
                rows = ReadRows(context.Dte);
            }
            catch (InvalidOperationException)
            {
                rows = [];
            }

            if (rows.Count == 0)
            {
                rows = ReadBuildOutputRows(context.Dte);
            }
        }
        else
        {
            rows = await WaitForRowsAsync(context, timeoutMilliseconds, intellisenseReady: waitForIntellisense).ConfigureAwait(true);
            if (rows.Count == 0)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);
                rows = ReadBuildOutputRows(context.Dte);
            }
        }

        if (!quickSnapshot)
        {
            var bestPracticeRows = await RefreshBestPracticeDiagnosticsAsync(context, rows).ConfigureAwait(true);
            if (bestPracticeRows.Count > 0)
            {
                rows = MergeRows(rows, bestPracticeRows);
            }
        }

        var filteredRows = ApplyQuery(rows, query).ToArray();
        var severityCounts = CreateSeverityCounts();
        foreach (var row in filteredRows)
        {
            severityCounts[(string)row[SeverityKey]!]++;
        }

        var totalSeverityCounts = CreateSeverityCounts();
        foreach (var row in rows)
        {
            totalSeverityCounts[(string)row[SeverityKey]!]++;
        }

        return new JObject
        {
            ["count"] = filteredRows.Length,
            ["totalCount"] = rows.Count,
            ["severityCounts"] = JObject.FromObject(severityCounts),
            ["totalSeverityCounts"] = JObject.FromObject(totalSeverityCounts),
            ["hasErrors"] = severityCounts["Error"] > 0,
            ["hasWarnings"] = severityCounts["Warning"] > 0,
            ["filter"] = query?.ToJson() ?? [],
            ["rows"] = new JArray(filteredRows),
            ["groups"] = BuildGroups(filteredRows, query?.GroupBy),
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

        var bestPracticeCandidateFiles = GetBestPracticeCandidateFiles(context.Dte, rows ?? []);
        var bestPracticeRows = await Task.Run(() => AnalyzeBestPracticeFindings(bestPracticeCandidateFiles), context.CancellationToken).ConfigureAwait(false);
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);
        PublishBestPracticeRows(context.Dte, bestPracticeRows);
        return bestPracticeRows;
    }

    private static IReadOnlyList<string> GetBestPracticeCandidateFiles(DTE2 dte, IReadOnlyList<JObject> rows)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var files = rows
            .Select(row => row["file"]?.ToString())
            .OfType<string>()
            .Where(IsBestPracticeCandidateFile)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxBestPracticeFiles)
            .ToList();

        if (files.Count == 0)
        {
            foreach (Document document in dte.Documents)
            {
                var fullName = document.FullName;
                if (!IsBestPracticeCandidateFile(fullName))
                {
                    continue;
                }

                if (files.Contains(fullName, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                files.Add(fullName);
                if (files.Count >= MaxBestPracticeFiles)
                {
                    break;
                }
            }
        }

        return files;
    }

    private static bool IsBestPracticeCandidateFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        foreach (var fragment in IgnoredBestPracticePathFragments)
        {
            if (fullPath.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }
        }

        var fileName = Path.GetFileName(fullPath);
        if (fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var extension = Path.GetExtension(fullPath);
        return !string.IsNullOrWhiteSpace(extension) && BestPracticeCodeExtensions.Contains(extension);
    }

    private enum CodeLanguage { Unknown, CSharp, Cpp, Python }

    private static CodeLanguage GetLanguage(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".cs" or ".vb" or ".fs" => CodeLanguage.CSharp,
            ".c" or ".cc" or ".cpp" or ".cxx" or ".h" or ".hh" or ".hpp" or ".hxx" => CodeLanguage.Cpp,
            ".py" => CodeLanguage.Python,
            _ => CodeLanguage.Unknown,
        };
    }

    private static bool IsHeaderFile(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext is ".h" or ".hh" or ".hpp" or ".hxx";
    }

    private static IReadOnlyList<JObject> AnalyzeBestPracticeFindings(IReadOnlyList<string> files)
    {
        var findings = new List<JObject>();

        foreach (var file in files)
        {
            var content = SafeReadFile(file);
            var perFileFindings = 0;
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            var language = GetLanguage(file);

            // Cross-language rules
            IEnumerable<JObject> fileFindings = FindRepeatedStringLiterals(file, content)
                .Concat(FindMagicNumbers(file, content));

            // Language-specific rules
            if (language == CodeLanguage.CSharp)
            {
                fileFindings = fileFindings
                    .Concat(FindSuspiciousRoundDown(file, content))
                    .Concat(FindEmptyCatchBlocks(file, content))
                    .Concat(FindAsyncVoid(file, content));
            }
            else if (language == CodeLanguage.Cpp)
            {
                fileFindings = fileFindings
                    .Concat(FindRawDelete(file, content))
                    .Concat(FindCStyleCasts(file, content));
                if (IsHeaderFile(file))
                {
                    fileFindings = fileFindings.Concat(FindUsingNamespaceInHeader(file, content));
                }
            }
            else if (language == CodeLanguage.Python)
            {
                fileFindings = fileFindings
                    .Concat(FindBareExcept(file, content))
                    .Concat(FindMutableDefaultArgs(file, content))
                    .Concat(FindImportStar(file, content));
            }

            foreach (var finding in fileFindings)
            {
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

    private static IEnumerable<JObject> FindRepeatedStringLiterals(string file, string content)
    {
        var occurrences = StringLiteralPattern.Matches(content)
            .Cast<Match>()
            .GroupBy(match => match.Groups[1].Value)
            .Where(group => group.Count() >= RepeatedStringThreshold)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(MaxBestPracticeFindingsPerFile);

        foreach (var repeated in occurrences)
        {
            yield return CreateBestPracticeRow(
                code: "BP1001",
                message: $"String literal '{repeated.Key}' is repeated {repeated.Count()} times. Extract a constant.",
                file: file,
                line: GetLineNumber(content, repeated.First().Index),
                symbol: repeated.Key,
                helpUri: BP1001HelpUri);
        }
    }

    private static IEnumerable<JObject> FindMagicNumbers(string file, string content)
    {
        var matches = NumberLiteralPattern.Matches(content)
            .Cast<Match>()
            .Select(match =>
            {
                var value = match.Groups["value"].Value;
                return new { match, value };
            })
            .Where(item => item.value is not "0" and not "1" and not "-1")
            .GroupBy(item => item.value)
            .Where(group => group.Count() >= RepeatedNumberThreshold)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(MaxBestPracticeFindingsPerFile);

        foreach (var repeated in matches)
        {
            yield return CreateBestPracticeRow(
                code: "BP1002",
                message: $"Numeric literal '{repeated.Key}' appears {repeated.Count()} times. Replace magic numbers with named constants.",
                file: file,
                line: GetLineNumber(content, repeated.First().match.Index),
                symbol: repeated.Key,
                helpUri: BP1002HelpUri);
        }
    }

    private static IEnumerable<JObject> FindSuspiciousRoundDown(string file, string content)
    {
        var lines = content.Split(["\r\n", "\n"], StringSplitOptions.None);
        var findingCount = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var roundDownMatch = SuspiciousRoundDownPattern.Match(line);
            if (roundDownMatch.Success)
            {
                yield return CreateBestPracticeRow(
                    code: "BP1003",
                    message: $"Math.{roundDownMatch.Groups["op"].Value} detected. Verify this is intentional rounding and not a workaround for integer overflow or type mismatch.",
                    file: file,
                    line: i + 1,
                    symbol: $"Math.{roundDownMatch.Groups["op"].Value}",
                    helpUri: BP1003HelpUri);
                findingCount++;
                if (findingCount >= MaxSuppressionFindingsPerFile)
                {
                    yield break;
                }
            }
        }
    }

    // ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ BP1004: Empty catch block (C#) ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬

    private static IEnumerable<JObject> FindEmptyCatchBlocks(string file, string content)
    {
        var matches = EmptyCatchBlockPattern.Matches(content);
        var findingCount = 0;
        foreach (Match match in matches)
        {
            yield return CreateBestPracticeRow(
                code: "BP1004",
                message: "Empty catch block swallows exceptions silently. Log or rethrow.",
                file: file,
                line: GetLineNumber(content, match.Index),
                symbol: "catch",
                helpUri: BP1004HelpUri);
            findingCount++;
            if (findingCount >= MaxSuppressionFindingsPerFile)
            {
                yield break;
            }
        }
    }

    // ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ BP1005: async void (C#) ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬

    private static IEnumerable<JObject> FindAsyncVoid(string file, string content)
    {
        var matches = AsyncVoidPattern.Matches(content);
        var findingCount = 0;
        foreach (Match match in matches)
        {
            var methodName = match.Groups[1].Value;
            yield return CreateBestPracticeRow(
                code: "BP1005",
                message: $"'async void {methodName}' has unobservable exceptions. Use 'async Task' instead.",
                file: file,
                line: GetLineNumber(content, match.Index),
                symbol: methodName,
                helpUri: BP1005HelpUri);
            findingCount++;
            if (findingCount >= MaxSuppressionFindingsPerFile)
            {
                yield break;
            }
        }
    }

    // ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ BP1006: Raw delete (C++) ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬

    private static IEnumerable<JObject> FindRawDelete(string file, string content)
    {
        var matches = RawDeletePattern.Matches(content);
        var findingCount = 0;
        foreach (Match match in matches)
        {
            var op = match.Groups[1].Success ? "delete[]" : "delete";
            yield return CreateBestPracticeRow(
                code: "BP1006",
                message: $"Raw '{op}' detected. Prefer smart pointers (std::unique_ptr, std::shared_ptr).",
                file: file,
                line: GetLineNumber(content, match.Index),
                symbol: op,
                helpUri: BP1006HelpUri);
            findingCount++;
            if (findingCount >= MaxSuppressionFindingsPerFile)
            {
                yield break;
            }
        }
    }

    // ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ BP1007: using namespace in header (C++) ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬

    private static IEnumerable<JObject> FindUsingNamespaceInHeader(string file, string content)
    {
        var matches = UsingNamespacePattern.Matches(content);
        var findingCount = 0;
        foreach (Match match in matches)
        {
            var ns = match.Groups[1].Value;
            yield return CreateBestPracticeRow(
                code: "BP1007",
                message: $"'using namespace {ns}' in header file pollutes the global namespace of every includer.",
                file: file,
                line: GetLineNumber(content, match.Index),
                symbol: ns,
                helpUri: BP1007HelpUri);
            findingCount++;
            if (findingCount >= MaxSuppressionFindingsPerFile)
            {
                yield break;
            }
        }
    }

    // ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ BP1008: C-style cast (C++) ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬

    private static IEnumerable<JObject> FindCStyleCasts(string file, string content)
    {
        var matches = CStyleCastPattern.Matches(content);
        var findingCount = 0;
        foreach (Match match in matches)
        {
            yield return CreateBestPracticeRow(
                code: "BP1008",
                message: $"C-style cast '{match.Value.TrimEnd()}' detected. Prefer static_cast, reinterpret_cast, or const_cast.",
                file: file,
                line: GetLineNumber(content, match.Index),
                symbol: match.Value.Trim(),
                helpUri: BP1008HelpUri);
            findingCount++;
            if (findingCount >= MaxSuppressionFindingsPerFile)
            {
                yield break;
            }
        }
    }

    // ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ BP1009: Bare except (Python) ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬

    private static IEnumerable<JObject> FindBareExcept(string file, string content)
    {
        var matches = BareExceptPattern.Matches(content);
        var findingCount = 0;
        foreach (Match match in matches)
        {
            yield return CreateBestPracticeRow(
                code: "BP1009",
                message: "Bare 'except:' catches all exceptions including SystemExit and KeyboardInterrupt. Catch specific exceptions.",
                file: file,
                line: GetLineNumber(content, match.Index),
                symbol: "except",
                helpUri: BP1009HelpUri);
            findingCount++;
            if (findingCount >= MaxSuppressionFindingsPerFile)
            {
                yield break;
            }
        }
    }

    // ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ BP1010: Mutable default argument (Python) ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬

    private static IEnumerable<JObject> FindMutableDefaultArgs(string file, string content)
    {
        var matches = MutableDefaultArgPattern.Matches(content);
        var findingCount = 0;
        foreach (Match match in matches)
        {
            var funcName = match.Groups[1].Value;
            yield return CreateBestPracticeRow(
                code: "BP1010",
                message: $"Function '{funcName}' uses a mutable default argument. Use None and initialize inside the function.",
                file: file,
                line: GetLineNumber(content, match.Index),
                symbol: funcName,
                helpUri: BP1010HelpUri);
            findingCount++;
            if (findingCount >= MaxSuppressionFindingsPerFile)
            {
                yield break;
            }
        }
    }

    // ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ BP1011: from X import * (Python) ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬

    private static IEnumerable<JObject> FindImportStar(string file, string content)
    {
        var matches = ImportStarPattern.Matches(content);
        var findingCount = 0;
        foreach (Match match in matches)
        {
            var module = match.Groups[1].Value;
            yield return CreateBestPracticeRow(
                code: "BP1011",
                message: $"'from {module} import *' pollutes the namespace. Import specific names.",
                file: file,
                line: GetLineNumber(content, match.Index),
                symbol: module,
                helpUri: BP1011HelpUri);
            findingCount++;
            if (findingCount >= MaxSuppressionFindingsPerFile)
            {
                yield break;
            }
        }
    }

    private static JObject CreateBestPracticeRow(string code, string message, string file, int line, string symbol, string helpUri = "")
    {
        var row = new JObject
        {
            [SeverityKey] = WarningSeverity,
            [CodeKey] = code,
            [CodeFamilyKey] = BestPracticeCategory,
            [ToolKey] = BestPracticeCategory,
            [MessageKey] = message,
            [ProjectKey] = string.Empty,
            [FileKey] = file,
            [LineKey] = line,
            [ColumnKey] = 1,
            [SymbolsKey] = new JArray(symbol),
            [SourceKey] = BestPracticeCategory,
        };
        if (!string.IsNullOrEmpty(helpUri))
        {
            row[HelpUriKey] = helpUri;
        }

        return row;
    }

    private void PublishBestPracticeRows(DTE2 dte, IReadOnlyList<JObject> rows)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_bestPracticeProvider.Tasks.Count > 0)
        {
            _bestPracticeProvider.Tasks.Clear();
            _bestPracticeProvider.Refresh();
        }

        if (TryEnsureBestPracticeTableSource() && _bestPracticeTableSource is not null)
        {
            _bestPracticeTableSource.UpdateRows(rows);
            if (rows.Count > 0)
            {
                ShowErrorListWindow(dte);
            }

            return;
        }

        _bestPracticeProvider.Tasks.Clear();
        foreach (var row in rows)
        {
            var task = new ErrorTask
            {
                Category = TaskCategory.BuildCompile,
                ErrorCategory = MapTaskErrorCategory(GetRowString(row, SeverityKey)),
                Text = GetRowString(row, MessageKey),
                Document = GetRowString(row, FileKey),
                Line = Math.Max(0, (GetNullableRowInt(row, LineKey) ?? 1) - 1),
                Column = Math.Max(0, (GetNullableRowInt(row, ColumnKey) ?? 1) - 1),
            };
            task.Navigate += (_, e) => NavigateToTask(dte, task, e);
            _bestPracticeProvider.Tasks.Add(task);
        }

        _bestPracticeProvider.Refresh();
        if (rows.Count > 0)
        {
            ShowErrorListWindow(dte);
            _bestPracticeProvider.Show();
            _bestPracticeProvider.BringToFront();
        }
    }

    private ITableManagerProvider? GetTableManagerProvider()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var serviceProvider = (System.IServiceProvider)_package;
        var componentModel = serviceProvider.GetService(typeof(SComponentModel)) as IComponentModel;
        return componentModel?.DefaultExportProvider.GetExportedValueOrDefault<ITableManagerProvider>();
    }

    private static void ShowErrorListWindow(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        EnsureErrorListWindow(dte);
        try
        {
            TryGetErrorListWindow(dte)?.Activate();
        }
        catch (Exception ex)
        {
            LogNonCriticalException(ex);
        }
    }

    private bool TryEnsureBestPracticeTableSource()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_bestPracticeTableSourceRegistered)
        {
            return true;
        }

        var tableManagerProvider = GetTableManagerProvider();
        if (tableManagerProvider is null)
        {
            return false;
        }

        var tableSource = new BestPracticeTableDataSource();
        var tableManager = tableManagerProvider.GetTableManager(StandardTables.ErrorsTable);
        if (!tableManager.AddSource(tableSource, BestPracticeTableColumns))
        {
            return false;
        }

        _bestPracticeTableSource = tableSource;
        _bestPracticeTableSourceRegistered = true;
        return true;
    }

    private static void NavigateToTask(DTE2 dte, ErrorTask task, EventArgs _)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            dte.ItemOperations.OpenFile(task.Document);
            if (dte.ActiveDocument?.Selection is TextSelection selection)
            {
                selection.GotoLine(task.Line + 1, Select: false);
                selection.MoveToLineAndOffset(task.Line + 1, Math.Max(1, task.Column + 1), Extend: false);
            }
        }
        catch (Exception ex)
        {
            LogNonCriticalException(ex);
        }
    }

    private static __VSERRORCATEGORY MapVisualStudioErrorCategory(string? severity)
    {
        return NormalizeSeverity(severity) switch
        {
            "Warning" => __VSERRORCATEGORY.EC_WARNING,
            "Message" => __VSERRORCATEGORY.EC_MESSAGE,
            _ => __VSERRORCATEGORY.EC_ERROR,
        };
    }
    private static TaskErrorCategory MapTaskErrorCategory(string? severity)
    {
        return NormalizeSeverity(severity) switch
        {
            "Warning" => TaskErrorCategory.Warning,
            "Message" => TaskErrorCategory.Message,
            _ => TaskErrorCategory.Error,
        };
    }

    private static IReadOnlyList<JObject> MergeRows(IReadOnlyList<JObject> rows, IReadOnlyList<JObject> additionalRows)
    {
        return [.. rows
            .Concat(additionalRows)
            .GroupBy(CreateDiagnosticIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())];
    }

    private static int GetLineNumber(string content, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < content.Length; i++)
        {
            if (content[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static Dictionary<string, int> CreateSeverityCounts()
    {
        return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Error"] = 0,
            ["Warning"] = 0,
            ["Message"] = 0,
        };
    }

    private async Task<IReadOnlyList<JObject>> WaitForRowsAsync(IdeCommandContext context, int timeoutMilliseconds, bool intellisenseReady = false)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);
        EnsureErrorListWindow(context.Dte);

        var timeout = timeoutMilliseconds > 0 ? timeoutMilliseconds : DefaultWaitTimeoutMilliseconds;
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeout);
        var lastRows = Array.Empty<JObject>();
        int? lastCount = null;
        var stableSamples = 0;
        // When IntelliSense has already confirmed ready, one stable read is sufficient.
        var requiredStableSamples = intellisenseReady ? 1 : StableSampleCount;

        while (DateTimeOffset.UtcNow < deadline)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<JObject>? rows = null;
            try
            {
                rows = ReadRows(context.Dte);
            }
            catch (InvalidOperationException)
            {
                EnsureErrorListWindow(context.Dte);
            }

            if (rows is not null)
            {
                if (rows.Count != lastCount)
                {
                    lastCount = rows.Count;
                    stableSamples = 1;
                }
                else
                {
                    stableSamples++;
                }

                lastRows = [.. rows];
                // A clean solution should return promptly once the Error List is stable,
                // instead of waiting out the full timeout for a non-zero row count.
                if (stableSamples >= requiredStableSamples)
                {
                    return rows;
                }
            }

            await Task.Delay(PopulationPollIntervalMilliseconds, context.CancellationToken).ConfigureAwait(false);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);
        }

        return lastRows;
    }

    private IReadOnlyList<JObject> ReadRows(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (TryReadTableRows(out var tableRows))
        {
            return tableRows;
        }

        var window = TryGetErrorListWindow(dte);
        if (window?.Object is not ErrorList errorList)
        {
            throw new InvalidOperationException("Error List window is not available.");
        }

        var items = errorList.ErrorItems;
        var rows = new List<JObject>(items.Count);
        for (var i = 1; i <= items.Count; i++)
        {
            var item = items.Item(i);
            var severity = MapSeverity(item.ErrorLevel);
            var description = item.Description ?? string.Empty;
            var code = InferCode(description, item.Project ?? string.Empty, item.FileName ?? string.Empty, item.Line);
            rows.Add(new JObject
            {
                [SeverityKey] = severity,
                ["code"] = code,
                ["codeFamily"] = InferCodeFamily(code),
                ["tool"] = InferTool(code, description),
                ["message"] = description,
                ["project"] = item.Project ?? string.Empty,
                ["file"] = item.FileName ?? string.Empty,
                ["line"] = item.Line,
                ["column"] = item.Column,
                ["symbols"] = new JArray(ExtractSymbols(description)),
            });
        }

        return rows;
    }

    private bool TryReadTableRows(out IReadOnlyList<JObject> rows)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        rows = [];
        var tableManagerProvider = GetTableManagerProvider();
        if (tableManagerProvider is null)
        {
            return false;
        }

        var tableManager = tableManagerProvider.GetTableManager(StandardTables.ErrorsTable);
        if (tableManager.Sources.Count == 0)
        {
            return false;
        }

        using var collector = new ErrorTableCollector();
        var subscriptions = new List<IDisposable>();
        try
        {
            foreach (var source in tableManager.Sources)
            {
                subscriptions.Add(source.Subscribe(collector));
            }

            rows = collector.GetRows();
            return collector.HasData;
        }
        finally
        {
            foreach (var subscription in subscriptions)
            {
                subscription.Dispose();
            }
        }
    }

    private static JObject CreateRowFromTableEntry(ITableEntry entry)
    {
        return CreateRowFromTableValueReader(entry.TryGetValue);
    }

    private static JObject CreateRowFromTableSnapshot(ITableEntriesSnapshot snapshot, int index)
    {
        return CreateRowFromTableValueReader(TryGetValue);

        bool TryGetValue(string keyName, out object content) => snapshot.TryGetValue(index, keyName, out content);
    }

    private static JObject CreateRowFromTableValueReader(TableValueReader tryGetValue)
    {
        var message = GetTableString(tryGetValue, StandardTableKeyNames.Text, StandardTableKeyNames.FullText);
        var project = GetTableString(tryGetValue, StandardTableKeyNames.ProjectName);
        var file = GetTableString(tryGetValue, StandardTableKeyNames.Path, StandardTableKeyNames.DocumentName);
        var line = GetTableCoordinate(tryGetValue, StandardTableKeyNames.Line);
        var code = GetTableString(tryGetValue, StandardTableKeyNames.ErrorCode, StandardTableKeyNames.ErrorCodeToolTip);
        if (string.IsNullOrWhiteSpace(code))
        {
            code = InferCode(message, project, file, line);
        }

        return new JObject
        {
            [SeverityKey] = MapTableSeverity(tryGetValue),
            [CodeKey] = code,
            [CodeFamilyKey] = InferCodeFamily(code),
            [ToolKey] = GetTableString(tryGetValue, StandardTableKeyNames.BuildTool),
            [MessageKey] = message,
            [ProjectKey] = project,
            [FileKey] = file,
            [LineKey] = line,
            [ColumnKey] = GetTableCoordinate(tryGetValue, StandardTableKeyNames.Column),
            ["symbols"] = new JArray(ExtractSymbols(message)),
        };
    }

    private static string GetTableString(TableValueReader tryGetValue, params string[] keyNames)
    {
        foreach (var keyName in keyNames)
        {
            if (tryGetValue(keyName, out var content) && content is not null)
            {
                var value = content.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return string.Empty;
    }

    private static int GetTableCoordinate(TableValueReader tryGetValue, string keyName)
    {
        if (!tryGetValue(keyName, out var content) || !TryConvertTableValueToInt(content, out var rawValue))
        {
            return 1;
        }

        return Math.Max(1, rawValue + 1);
    }

    private static string MapTableSeverity(TableValueReader tryGetValue)
    {
        if (!tryGetValue(StandardTableKeyNames.ErrorSeverity, out var content) || !TryConvertTableValueToInt(content, out var rawValue))
        {
            return "Error";
        }

        return ((__VSERRORCATEGORY)rawValue) switch
        {
            __VSERRORCATEGORY.EC_WARNING => "Warning",
            __VSERRORCATEGORY.EC_MESSAGE => "Message",
            _ => "Error",
        };
    }

    private static bool TryConvertTableValueToInt(object? content, out int value)
    {
        if (content is null)
        {
            value = 0;
            return false;
        }

        switch (content)
        {
            case int intValue:
                value = intValue;
                return true;
            case short shortValue:
                value = shortValue;
                return true;
            case long longValue when longValue is >= int.MinValue and <= int.MaxValue:
                value = (int)longValue;
                return true;
            case byte byteValue:
                value = byteValue;
                return true;
            case sbyte sbyteValue:
                value = sbyteValue;
                return true;
            default:
                if (content.GetType().IsEnum)
                {
                    value = Convert.ToInt32(content, CultureInfo.InvariantCulture);
                    return true;
                }

                if (int.TryParse(content.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    value = parsed;
                    return true;
                }

                value = 0;
                return false;
        }
    }

    private static IReadOnlyList<JObject> ReadBuildOutputRows(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var pane = TryGetBuildOutputPane(dte);
        if (pane is null)
        {
            return [];
        }

        var text = TryReadBuildOutputText(dte, pane);
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var rows = new List<JObject>();
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (TryParseBuildOutputLine(line, out var row))
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    private static string TryReadBuildOutputText(DTE2 dte, OutputWindowPane pane)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        ActivateBuildOutputPane(dte, pane);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                return pane.TextDocument is TextDocument textDocument
                    ? ReadTextDocument(textDocument)
                    : string.Empty;
            }
            catch (COMException)
            {
                if (attempt == 0)
                {
                    ActivateBuildOutputPane(dte, pane);
                    continue;
                }

                return string.Empty;
            }
        }

        return string.Empty;
    }

    private static void EnsureErrorListWindow(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (TryGetErrorListWindow(dte)?.Object is ErrorList)
        {
            return;
        }

        try
        {
            dte.ExecuteCommand("View.ErrorList", string.Empty);
        }
        catch (Exception ex)
        {
            LogNonCriticalException(ex);
        }
    }

    private static void ActivateBuildOutputPane(DTE2 dte, OutputWindowPane pane)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            dte.ExecuteCommand("View.Output", string.Empty);
        }
        catch (Exception ex)
        {
            LogNonCriticalException(ex);
        }

        try
        {
            pane.Activate();
        }
        catch (Exception ex)
        {
            LogNonCriticalException(ex);
        }
    }

    private static OutputWindowPane? TryGetBuildOutputPane(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        foreach (OutputWindowPane pane in dte.ToolWindows.OutputWindow.OutputWindowPanes)
        {
            var paneName = pane.Name;
            foreach (var candidateName in BuildOutputPaneNames)
            {
                if (string.Equals(paneName, candidateName, StringComparison.OrdinalIgnoreCase))
                {
                    return pane;
                }
            }
        }

        return null;
    }

    private static string ReadTextDocument(TextDocument textDocument)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var start = textDocument.StartPoint.CreateEditPoint();
        return start.GetText(textDocument.EndPoint);
    }

    private static bool TryParseBuildOutputLine(string line, out JObject row)
    {
        row = null!;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var match = StructuredOutputPattern.Match(line);
        if (!match.Success)
        {
            match = MsBuildDiagnosticPattern.Match(line);
        }

        if (!match.Success)
        {
            return false;
        }

        var severity = NormalizeParsedSeverity(match.Groups["severity"].Value);
        var description = match.Groups["message"].Value.Trim();
        var project = match.Groups["project"].Value.Trim();
        var file = NormalizeFilePath(match.Groups["file"].Value.Trim());
        var lineNumber = ParseOptionalInt(match.Groups["line"].Value);
        var columnNumber = ParseOptionalInt(match.Groups["column"].Value);
        var code = NormalizeCode(match.Groups["code"].Value, description, project, file, lineNumber);

        row = new JObject
        {
            [SeverityKey] = severity,
            ["code"] = code,
            ["codeFamily"] = InferCodeFamily(code),
            ["tool"] = InferTool(code, description),
            ["message"] = description,
            ["project"] = project,
            ["file"] = file,
            ["line"] = lineNumber,
            ["column"] = columnNumber,
            ["symbols"] = new JArray(ExtractSymbols(description)),
            ["source"] = "build-output",
        };
        return true;
    }

    private static string NormalizeParsedSeverity(string severity)
    {
        return severity.Equals("error", StringComparison.OrdinalIgnoreCase) ? "Error" : "Warning";
    }

    private static string NormalizeFilePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        try
        {
            return PathNormalization.NormalizeFilePath(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Build output can include non-path tokens in the file column; keep the raw value
            // so diagnostics still flow without failing the entire error-list request.
            return value;
        }
    }

    private static int ParseOptionalInt(string value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private static string NormalizeCode(string explicitCode, string description, string project, string fileName, int line)
    {
        return !string.IsNullOrWhiteSpace(explicitCode)
            ? explicitCode
            : InferCode(description, project, fileName, line);
    }

    private static Window? TryGetErrorListWindow(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        return dte.Windows
            .Cast<Window>()
            .FirstOrDefault(IsErrorListWindow);
    }

    private static bool IsErrorListWindow(Window candidate)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return string.Equals(candidate.Caption, "Error List", StringComparison.OrdinalIgnoreCase);
    }

    private static string MapSeverity(vsBuildErrorLevel level)
    {
        return level switch
        {
            vsBuildErrorLevel.vsBuildErrorLevelHigh => "Error",
            vsBuildErrorLevel.vsBuildErrorLevelMedium => "Warning",
            _ => "Message",
        };
    }

    private static string InferCode(string description, string project, string fileName, int line)
    {
        var explicitCode = ExtractExplicitCode(description);
        if (!string.IsNullOrWhiteSpace(explicitCode))
        {
            return explicitCode;
        }

        if (description.IndexOf("identifier \"", StringComparison.OrdinalIgnoreCase) >= 0 &&
            description.IndexOf("\" is undefined", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "E0020";
        }

        if (description.IndexOf("can be made static", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "VCR003";
        }

        if (description.IndexOf("can be made const", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "VCR001";
        }

        if (description.IndexOf("Return value ignored", StringComparison.OrdinalIgnoreCase) >= 0 &&
            description.IndexOf("UnregisterWaitEx", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "C6031";
        }

        if (description.IndexOf("PCH warning:", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "Int-make";
        }

        if (description.IndexOf("doesn't deduce references", StringComparison.OrdinalIgnoreCase) >= 0 &&
            description.IndexOf("possibly unintended copy", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "lnt-accidental-copy";
        }

        if (description.IndexOf("cannot open file '", StringComparison.OrdinalIgnoreCase) >= 0 &&
            IsLinkerContext(project, fileName, line))
        {
            return "LNK1104";
        }

        return string.Empty;
    }

    private static string ExtractExplicitCode(string description)
    {
        var match = ExplicitCodePattern.Match(description);
        return match.Success ? NormalizeCode(match.Value) : string.Empty;
    }

    private static string NormalizeCode(string code)
    {
        if (code.StartsWith("LINK", StringComparison.OrdinalIgnoreCase) &&
            code.Length > LinkerCodePrefixLength &&
            int.TryParse(code.Substring(LinkerCodePrefixLength), NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return "LNK" + code.Substring(LinkerCodePrefixLength);
        }

        if (code.StartsWith("lnt-", StringComparison.OrdinalIgnoreCase))
        {
            return code.ToLowerInvariant();
        }

        return code.ToUpperInvariant();
    }

    private static string InferCodeFamily(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return string.Empty;
        }

        if (code.StartsWith("BP", StringComparison.OrdinalIgnoreCase))
        {
            return "best-practice";
        }

        if (code.StartsWith("LNK", StringComparison.OrdinalIgnoreCase))
        {
            return "linker";
        }

        if (code.StartsWith("MSB", StringComparison.OrdinalIgnoreCase))
        {
            return "msbuild";
        }

        if (code.StartsWith("VCR", StringComparison.OrdinalIgnoreCase))
        {
            return "analyzer";
        }

        if (code.StartsWith("lnt-", StringComparison.OrdinalIgnoreCase))
        {
            return "linter";
        }

        if (code.StartsWith("C", StringComparison.OrdinalIgnoreCase) ||
            code.StartsWith("E", StringComparison.OrdinalIgnoreCase))
        {
            return "compiler";
        }

        return "other";
    }

    private static string InferTool(string code, string description)
    {
        var family = InferCodeFamily(code);
        if (!string.IsNullOrWhiteSpace(family))
        {
            return family;
        }

        if (description.IndexOf("IntelliSense", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "intellisense";
        }

        return "diagnostic";
    }

    private static IReadOnlyList<string> ExtractSymbols(string description)
    {
        var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in Regex.Matches(description, "\"([^\"]+)\"|'([^']+)'"))
        {
            var value = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            if (LooksLikeSymbol(value))
            {
                symbols.Add(value);
            }
        }

        foreach (Match match in Regex.Matches(description, @"\b[A-Za-z_~][A-Za-z0-9_:<>~]*\b"))
        {
            var value = match.Value;
            if (LooksLikeSymbol(value))
            {
                symbols.Add(value);
            }
        }

        return [.. symbols.Take(8)];
    }

    private static bool LooksLikeSymbol(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 2)
        {
            return false;
        }

        return value.Contains("::", StringComparison.Ordinal) ||
            value.Contains("_", StringComparison.Ordinal) ||
            char.IsUpper(value[0]) ||
            value.StartsWith("C", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("E", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<JObject> ApplyQuery(IReadOnlyList<JObject> rows, ErrorListQuery? query)
    {
        if (query is null)
        {
            return rows;
        }

        IEnumerable<JObject> filtered = rows;

        if (!string.IsNullOrWhiteSpace(query.Severity) &&
            !string.Equals(query.Severity, "all", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(row => string.Equals(
                GetRowString(row, SeverityKey),
                NormalizeSeverity(query.Severity),
                StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query.Code))
        {
            filtered = filtered.Where(row => GetRowString(row, CodeKey)
                .StartsWith(query.Code, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query.Project))
        {
            filtered = filtered.Where(row => GetRowString(row, ProjectKey)
                .IndexOf(query.Project, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        if (!string.IsNullOrWhiteSpace(query.Path))
        {
            filtered = filtered.Where(row => GetRowString(row, FileKey)
                .IndexOf(query.Path, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            filtered = filtered.Where(row => GetRowString(row, MessageKey)
                .IndexOf(query.Text, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        if (query.Max > 0)
        {
            filtered = filtered.Take(query.Max.Value);
        }

        return [.. filtered];
    }

    private delegate bool TableValueReader(string keyName, out object content);

    private static string CreateFindingIdentity(JObject row)
    {
        return string.Join(
            "|",
            GetRowString(row, CodeKey),
            GetRowString(row, FileKey),
            (GetNullableRowInt(row, LineKey) ?? 0).ToString(CultureInfo.InvariantCulture));
    }

    private static string CreateDiagnosticIdentity(JObject row)
    {
        return string.Join(
            "|",
            GetRowString(row, SeverityKey),
            GetRowString(row, CodeKey),
            GetRowString(row, FileKey),
            (GetNullableRowInt(row, LineKey) ?? 0).ToString(CultureInfo.InvariantCulture),
            GetRowString(row, MessageKey));
    }

    private static string GetRowString(JObject row, string key)
    {
        return (string?)row[key] ?? string.Empty;
    }

    private static int? GetNullableRowInt(JObject row, string key)
    {
        return (int?)row[key];
    }

    private static void LogNonCriticalException(Exception ex)
    {
        System.Diagnostics.Debug.WriteLine(ex);
    }

    private static string NormalizeSeverity(string? severity)
    {
        return severity?.ToLowerInvariant() switch
        {
            "error" => "Error",
            "warning" => "Warning",
            "message" => "Message",
            _ => severity ?? string.Empty,
        };
    }

    private static JArray BuildGroups(IReadOnlyList<JObject> rows, string? groupBy)
    {
        if (string.IsNullOrWhiteSpace(groupBy))
        {
            return [];
        }

        var groupKey = groupBy!;
        Func<JObject, string> keySelector = groupKey.ToLowerInvariant() switch
        {
            "code" => row => (string?)row["code"] ?? string.Empty,
            "file" => row => (string?)row["file"] ?? string.Empty,
            "project" => row => (string?)row["project"] ?? string.Empty,
            "tool" => row => (string?)row["tool"] ?? string.Empty,
            _ => static _ => string.Empty,
        };

        if (groupKey is not ("code" or "file" or "project" or "tool"))
        {
            return [];
        }

        return new JArray(rows
            .GroupBy(keySelector, StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new JObject
            {
                ["key"] = group.Key,
                ["groupBy"] = groupKey,
                ["count"] = group.Count(),
                ["sampleMessage"] = (string?)group.First()["message"] ?? string.Empty,
                ["sampleFile"] = (string?)group.First()["file"] ?? string.Empty,
                ["sampleCode"] = (string?)group.First()["code"] ?? string.Empty,
            }));
    }

    private sealed class ErrorTableCollector : ITableDataSink, IDisposable
    {
        private readonly List<ITableEntry> _entries = [];
        private readonly List<ITableEntriesSnapshot> _snapshots = [];
        private readonly List<ITableEntriesSnapshotFactory> _factories = [];

        public bool IsStable { get; set; } = true;

        public bool HasData => _entries.Count > 0 || _snapshots.Count > 0 || _factories.Count > 0;

        public IReadOnlyList<JObject> GetRows()
        {
            var rows = new List<JObject>();

            foreach (var entry in _entries)
            {
                rows.Add(CreateRowFromTableEntry(entry));
            }

            foreach (var snapshot in _snapshots)
            {
                AddSnapshotRows(rows, snapshot);
            }

            foreach (var factory in _factories)
            {
                AddSnapshotRows(rows, factory.GetCurrentSnapshot());
            }

            return [.. rows
                .GroupBy(CreateDiagnosticIdentity, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())];
        }

        public void AddEntries(IReadOnlyList<ITableEntry> newEntries, bool removeAllEntries)
        {
            if (removeAllEntries)
            {
                _entries.Clear();
            }

            _entries.AddRange(newEntries);
        }

        public void RemoveEntries(IReadOnlyList<ITableEntry> oldEntries)
        {
            foreach (var entry in oldEntries)
            {
                _entries.Remove(entry);
            }
        }

        public void ReplaceEntries(IReadOnlyList<ITableEntry> oldEntries, IReadOnlyList<ITableEntry> newEntries)
        {
            RemoveEntries(oldEntries);
            AddEntries(newEntries, removeAllEntries: false);
        }

        public void RemoveAllEntries()
        {
            _entries.Clear();
        }

        public void AddSnapshot(ITableEntriesSnapshot newSnapshot, bool removeAllSnapshots)
        {
            if (removeAllSnapshots)
            {
                _snapshots.Clear();
            }

            _snapshots.Add(newSnapshot);
        }

        public void RemoveSnapshot(ITableEntriesSnapshot oldSnapshot)
        {
            _snapshots.Remove(oldSnapshot);
        }

        public void RemoveAllSnapshots()
        {
            _snapshots.Clear();
        }

        public void ReplaceSnapshot(ITableEntriesSnapshot oldSnapshot, ITableEntriesSnapshot newSnapshot)
        {
            RemoveSnapshot(oldSnapshot);
            AddSnapshot(newSnapshot, removeAllSnapshots: false);
        }

        public void AddFactory(ITableEntriesSnapshotFactory newFactory, bool removeAllFactories)
        {
            if (removeAllFactories)
            {
                _factories.Clear();
            }

            _factories.Add(newFactory);
        }

        public void RemoveFactory(ITableEntriesSnapshotFactory oldFactory)
        {
            _factories.Remove(oldFactory);
        }

        public void ReplaceFactory(ITableEntriesSnapshotFactory oldFactory, ITableEntriesSnapshotFactory newFactory)
        {
            RemoveFactory(oldFactory);
            AddFactory(newFactory, removeAllFactories: false);
        }

        public void FactorySnapshotChanged(ITableEntriesSnapshotFactory? factory)
        {
        }

        public void RemoveAllFactories()
        {
            _factories.Clear();
        }

        public void Dispose()
        {
        }

        private static void AddSnapshotRows(List<JObject> rows, ITableEntriesSnapshot snapshot)
        {
            for (var index = 0; index < snapshot.Count; index++)
            {
                rows.Add(CreateRowFromTableSnapshot(snapshot, index));
            }
        }
    }

    private sealed class BestPracticeTableDataSource : ITableDataSource
    {
        private readonly BestPracticeSnapshotFactory _factory = new();
        private ITableDataSink? _sink;

        public string DisplayName => "VS IDE Bridge Best Practices";

        public string Identifier { get; } = $"vs-ide-bridge-best-practice-{Guid.NewGuid():N}";

        public string SourceTypeIdentifier => StandardTableDataSources.ErrorTableDataSource;

        public IDisposable Subscribe(ITableDataSink sink)
        {
            _sink = sink;
            sink.AddFactory(_factory, removeAllFactories: false);
            return new Subscription(this, sink);
        }

        public void UpdateRows(IReadOnlyList<JObject> rows)
        {
            _factory.UpdateRows(rows);
            _sink?.FactorySnapshotChanged(_factory);
        }

        private void Unsubscribe(ITableDataSink sink)
        {
            if (!ReferenceEquals(_sink, sink))
            {
                return;
            }

            sink.RemoveFactory(_factory);
            _sink = null;
        }

        private sealed class Subscription(BestPracticeTableDataSource owner, ITableDataSink sink) : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                owner.Unsubscribe(sink);
                _disposed = true;
            }
        }
    }

    private sealed class BestPracticeSnapshotFactory : ITableEntriesSnapshotFactory
    {
        private BestPracticeTableEntriesSnapshot _current = new([], 0);

        public int CurrentVersionNumber => _current.VersionNumber;

        public ITableEntriesSnapshot GetCurrentSnapshot() => _current;

        public ITableEntriesSnapshot GetSnapshot(int versionNumber)
        {
            return _current;
        }

        public void UpdateRows(IReadOnlyList<JObject> rows)
        {
            var entries = rows.Select(BestPracticeTableEntry.FromRow).ToArray();
            _current = new BestPracticeTableEntriesSnapshot(entries, _current.VersionNumber + 1);
        }

        public void Dispose()
        {
        }
    }

    private sealed class BestPracticeTableEntriesSnapshot(IReadOnlyList<BestPracticeTableEntry> entries, int versionNumber) : ITableEntriesSnapshot
    {
        private readonly IReadOnlyList<BestPracticeTableEntry> _entries = entries;
        private readonly Dictionary<string, int> _entryIndexes = entries
            .Select((entry, index) => new KeyValuePair<string, int>(entry.StableKey, index))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        public int Count => _entries.Count;

        public int VersionNumber { get; } = versionNumber;

        public int IndexOf(int currentIndex, ITableEntriesSnapshot newSnapshot)
        {
            if ((uint)currentIndex >= (uint)_entries.Count || newSnapshot is not BestPracticeTableEntriesSnapshot typedSnapshot)
            {
                return -1;
            }

            return typedSnapshot._entryIndexes.TryGetValue(_entries[currentIndex].StableKey, out var newIndex)
                ? newIndex
                : -1;
        }

        public void StartCaching()
        {
        }

        public void StopCaching()
        {
        }

        public bool TryGetValue(int index, string keyName, out object content)
        {
            if ((uint)index >= (uint)_entries.Count)
            {
                content = null!;
                return false;
            }

            var entry = _entries[index];
            switch (keyName)
            {
                case StandardTableKeyNames.ErrorSeverity:
                    content = MapVisualStudioErrorCategory(entry.Severity);
                    return true;
                case StandardTableKeyNames.ErrorCode:
                case StandardTableKeyNames.ErrorCodeToolTip:
                    content = entry.Code;
                    return true;
                case StandardTableKeyNames.Text:
                    content = entry.Message;
                    return true;
                case StandardTableKeyNames.DocumentName:
                    content = Path.GetFileName(entry.File);
                    return true;
                case StandardTableKeyNames.Path:
                    content = entry.File;
                    return true;
                case StandardTableKeyNames.Line:
                    content = Math.Max(0, entry.Line - 1);
                    return true;
                case StandardTableKeyNames.Column:
                    content = Math.Max(0, entry.Column - 1);
                    return true;
                case StandardTableKeyNames.ProjectName:
                    content = entry.Project;
                    return true;
                case StandardTableKeyNames.BuildTool:
                    content = entry.Tool;
                    return true;
                case StandardTableKeyNames.ErrorSource:
                    content = Microsoft.VisualStudio.Shell.TableManager.ErrorSource.Build;
                    return true;
                case StandardTableKeyNames.HelpKeyword:
                    content = string.IsNullOrWhiteSpace(entry.Code) ? BestPracticeCategory : entry.Code;
                    return true;
                case StandardTableKeyNames.HelpLink:
                    content = entry.HelpUri;
                    return true;
                case StandardTableKeyNames.FullText:
                    content = string.IsNullOrWhiteSpace(entry.Code) ? entry.Message : $"{entry.Code}: {entry.Message}";
                    return true;
                default:
                    content = null!;
                    return false;
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class BestPracticeTableEntry(string severity, string code, string message, string file, int line, int column, string project, string tool, string helpUri)
    {
        public string Severity { get; } = severity;

        public string Code { get; } = code;

        public string Message { get; } = message;

        public string File { get; } = file;

        public int Line { get; } = line;

        public int Column { get; } = column;

        public string Project { get; } = project;

        public string Tool { get; } = tool;

        public string HelpUri { get; } = helpUri;

        public string StableKey => string.Join("|", Severity, Code, File, Line.ToString(CultureInfo.InvariantCulture), Column.ToString(CultureInfo.InvariantCulture), Message);

        public static BestPracticeTableEntry FromRow(JObject row)
        {
            return new BestPracticeTableEntry(
                string.IsNullOrEmpty(GetRowString(row, SeverityKey)) ? WarningSeverity : GetRowString(row, SeverityKey),
                GetRowString(row, CodeKey),
                GetRowString(row, MessageKey),
                GetRowString(row, FileKey),
                Math.Max(1, GetNullableRowInt(row, LineKey) ?? 1),
                Math.Max(1, GetNullableRowInt(row, ColumnKey) ?? 1),
                GetRowString(row, ProjectKey),
                string.IsNullOrEmpty(GetRowString(row, ToolKey)) ? BestPracticeCategory : GetRowString(row, ToolKey),
                GetRowString(row, HelpUriKey));
        }
    }
    private static bool IsLinkerContext(string project, string fileName, int line)
    {
        var normalizedFile = (fileName ?? string.Empty).Replace('/', '\\');
        if (normalizedFile.EndsWith("\\LINK", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.IsNullOrWhiteSpace(project) && string.IsNullOrWhiteSpace(fileName) && line <= 0;
    }
}














