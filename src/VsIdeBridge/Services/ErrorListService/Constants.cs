using System;
using Microsoft.VisualStudio.Shell.TableManager;
using static VsIdeBridge.Diagnostics.ErrorListConstants;

namespace VsIdeBridge.Services;

internal sealed partial class ErrorListService
{
    private static readonly TimeSpan TableCollectorQuietPeriod = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan TableCollectorMaxWait = TimeSpan.FromMilliseconds(750);

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
        GuidanceKey,
        SuggestedActionKey,
        LlmFixPromptKey,
        AuthorityKey,
    ];
}
