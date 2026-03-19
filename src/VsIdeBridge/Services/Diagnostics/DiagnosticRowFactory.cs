using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace VsIdeBridge.Services.Diagnostics;

internal static class DiagnosticRowFactory
{
    public static JObject CreateBestPracticeRow(string code, string message, string file, int line, string symbol, string helpUri = "")
    {
        var row = new JObject
        {
            [ErrorListConstants.SeverityKey] = ErrorListConstants.WarningSeverity,
            [ErrorListConstants.CodeKey] = code,
            [ErrorListConstants.CodeFamilyKey] = ErrorListConstants.BestPracticeCategory,
            [ErrorListConstants.ToolKey] = ErrorListConstants.BestPracticeCategory,
            [ErrorListConstants.MessageKey] = message,
            [ErrorListConstants.ProjectKey] = string.Empty,
            [ErrorListConstants.FileKey] = file,
            [ErrorListConstants.LineKey] = line,
            [ErrorListConstants.ColumnKey] = 1,
            [ErrorListConstants.SymbolsKey] = new JArray(symbol),
            [ErrorListConstants.SourceKey] = ErrorListConstants.BestPracticeCategory,
        };
        if (!string.IsNullOrEmpty(helpUri))
        {
            row[ErrorListConstants.HelpUriKey] = helpUri;
        }

        return row;
    }
}