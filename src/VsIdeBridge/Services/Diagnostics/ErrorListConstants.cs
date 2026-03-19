using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.Shell.TableManager;

namespace VsIdeBridge.Services.Diagnostics;

internal static class ErrorListConstants
{
    public const int StableSampleCount = 3;
    public const int PopulationPollIntervalMilliseconds = 2000;
    public const int DefaultWaitTimeoutMilliseconds = 90_000;
    public const int BuildOutputReadAttemptCount = CoordinateSuffixTrimLength;
    public const int CoordinateSuffixTrimLength = 2;
    public const int MaximumBuildOutputCoordinateCount = CoordinateSuffixTrimLength * CoordinateSuffixTrimLength;
    public const int MaxBestPracticeFiles = 512;
    public const int MaxBestPracticeFindingsPerFile = 25;
    public const int MinimumSymbolLength = CoordinateSuffixTrimLength;
    public const int DiagnosticLineQualityScore = CoordinateSuffixTrimLength * CoordinateSuffixTrimLength;
    public const int DiagnosticColumnQualityScore = CoordinateSuffixTrimLength;
    public const int RepeatedStringThreshold = MaxBestPracticeFindingsPerFile / MaxBestPracticeFindingsPerFile + CoordinateSuffixTrimLength * CoordinateSuffixTrimLength;
    public const int RepeatedNumberThreshold = CoordinateSuffixTrimLength * CoordinateSuffixTrimLength;
    public const int MaxSuppressionFindingsPerFile = RepeatedStringThreshold;
    public const int LinkerCodePrefixLength = CoordinateSuffixTrimLength * CoordinateSuffixTrimLength;
    public const int MaxFindingsPerFile = 100;
    public const int FileTooLongWarningThreshold = 1000;
    public const int FileTooLongErrorThreshold = 2000;
    public const int MethodTooLongThreshold = 100;
    public const int DeepNestingThreshold = MaxSuppressionFindingsPerFile;
    public const int CommentedOutCodeThreshold = MaxSuppressionFindingsPerFile;
    public const int GodClassMethodThreshold = 30;
    public const int GodClassFieldThreshold = 15;
    public const int PropertyBagPropertyThreshold = 8;
    public const int PropertyBagBehaviorThreshold = 2;
    public const int MacroOveruseThreshold = 15;
    public const int DynamicObjectThreshold = MaxSuppressionFindingsPerFile;

    public const string SeverityKey = "severity";
    public const string CodeKey = "code";
    public const string ProjectKey = "project";
    public const string FileKey = "file";
    public const string LineKey = "line";
    public const string ColumnKey = "column";
    public const string MessageKey = "message";
    public const string ToolKey = "tool";
    public const string CodeFamilyKey = "codeFamily";
    public const string SymbolsKey = "symbols";
    public const string SymbolKey = "symbol";
    public const string SourceKey = "source";
    public const string GuidanceKey = "guidance";
    public const string SuggestedActionKey = "suggestedAction";
    public const string HelpUriKey = "helpUri";
    public const string WarningSeverity = "Warning";
    public const string BestPracticeCategory = "best-practice";

    // Documentation URIs surfaced in MCP error output so AI assistants can link to the relevant guidance.
    public const string BP1001HelpUri = "https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions";
    public const string BP1002HelpUri = "https://learn.microsoft.com/en-us/dotnet/standard/design-guidelines/general-naming-conventions";
    public const string BP1003HelpUri = "https://learn.microsoft.com/en-us/dotnet/standard/exceptions/best-practices-for-exceptions";
    public const string BP1004HelpUri = "https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/quality-rules/ca1031";
    public const string BP1005HelpUri = "https://learn.microsoft.com/en-us/archive/msdn-magazine/2013/march/async-await-best-practices-in-asynchronous-programming";
    public const string BP1006HelpUri = "https://isocpp.github.io/CppCoreGuidelines/CppCoreGuidelines#r11-avoid-calling-new-and-delete-explicitly";
    public const string BP1007HelpUri = "https://isocpp.github.io/CppCoreGuidelines/CppCoreGuidelines#sf7-dont-write-using-namespace-at-global-scope-in-a-header-file";
    public const string BP1008HelpUri = "https://isocpp.github.io/CppCoreGuidelines/CppCoreGuidelines#es49-if-you-must-use-a-cast-use-a-named-cast";
    public const string BP1009HelpUri = "https://docs.python.org/3/howto/logging.html#exceptions";
    public const string BP1010HelpUri = "https://docs.python.org/3/reference/compound_stmts.html#function-definitions";
    public const string BP1011HelpUri = "https://peps.python.org/pep-0008/#imports";
    public const string BP1012HelpUri = "https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions";
    public const string BP1013HelpUri = "https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions";
    public const string BP1014HelpUri = "https://learn.microsoft.com/en-us/dotnet/standard/design-guidelines/general-naming-conventions";
    public const string BP1015HelpUri = "https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions";
    public const string BP1016HelpUri = "https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions";
    public const string BP1017HelpUri = "https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions";
    public const string BP1018HelpUri = "https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions";
    public const string BP1019HelpUri = "https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/using-objects";
    public const string BP1020HelpUri = "https://learn.microsoft.com/en-us/dotnet/api/system.datetime.utcnow";
    public const string BP1021HelpUri = "https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/reference-types";
    public const string BP1022HelpUri = "https://isocpp.github.io/CppCoreGuidelines/CppCoreGuidelines#r11-avoid-calling-new-and-delete-explicitly";
    public const string BP1023HelpUri = "https://isocpp.github.io/CppCoreGuidelines/CppCoreGuidelines#p3-express-intent";
    public const string BP1024HelpUri = "https://isocpp.github.io/CppCoreGuidelines/CppCoreGuidelines#c133-avoid-protected-data";
    public const string BP1025HelpUri = "https://isocpp.github.io/CppCoreGuidelines/CppCoreGuidelines#con1-by-default-make-objects-immutable";
    public const string BP1026HelpUri = "https://peps.python.org/pep-0008/#programming-recommendations";
    public const string BP1027HelpUri = "https://learn.microsoft.com/en-us/dotnet/architecture/microservices/microservice-ddd-cqrs-patterns/microservice-application-layer-web-api-design";

    public static readonly string[] BestPracticeCodeExtensionValues = [".cs", ".vb", ".fs", ".c", ".cc", ".cpp", ".cxx", ".h", ".hh", ".hpp", ".hxx", ".py"];
    public static readonly HashSet<string> BestPracticeCodeExtensions = new(BestPracticeCodeExtensionValues, StringComparer.OrdinalIgnoreCase);
    public static readonly string[] IgnoredBestPracticePathFragments = ["\\.vs\\", "\\bin\\", "\\obj\\", "\\output\\"];
    public static readonly string[] BuildOutputPaneNames = ["Build", "Build Order"];
    public static readonly string[] BestPracticeTableColumns =
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
}
