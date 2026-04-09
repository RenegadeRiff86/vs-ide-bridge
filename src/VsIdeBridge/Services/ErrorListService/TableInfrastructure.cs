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
using static VsIdeBridge.Diagnostics.ErrorListPatterns;

namespace VsIdeBridge.Services;

internal sealed partial class ErrorListService
{
    private static JObject CreateBestPracticeRow(string code, string message, string file, int line, string symbol, string helpUri = "")
    {
        JObject row = new()
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

        // Prefer the table source because it preserves structured columns such as Project.
        if (TryEnsureBestPracticeTableSource() && _bestPracticeTableSource is not null)
        {
            _bestPracticeTableSource.UpdateRows(rows);

            _bestPracticeProvider.Refresh();
            if (rows.Count > 0)
            {
                ShowErrorListWindow(dte);
                _bestPracticeProvider.Show();
                _bestPracticeProvider.BringToFront();
            }

            return;
        }

        _bestPracticeProvider.Tasks.Clear();
        foreach (var row in rows)
        {
            ErrorTask task = new()
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

        System.IServiceProvider serviceProvider = (System.IServiceProvider)_package;
        IComponentModel? componentModel = serviceProvider.GetService(typeof(SComponentModel)) as IComponentModel;
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
        catch (COMException ex)
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

        ITableManagerProvider? tableManagerProvider = GetTableManagerProvider();
        if (tableManagerProvider is null)
        {
            return false;
        }

        BestPracticeTableDataSource tableSource = new();
        ITableManager tableManager = tableManagerProvider.GetTableManager(StandardTables.ErrorsTable);
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
        catch (COMException ex)
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
            .Select(SelectPreferredDiagnosticRow)];
    }

    private static IReadOnlyList<JObject> ExcludeBuildOutputRows(IReadOnlyList<JObject> rows)
    {
        return [.. rows.Where(row => !string.Equals((string?)row[SourceKey], "build-output", StringComparison.OrdinalIgnoreCase))];
    }

    private static int GetLineNumber(string content, int index)
    {
        int line = 1;
        for (int i = 0; i < index && i < content.Length; i++)
        {
            if (content[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static string GetLineAt(string content, int index)
    {
        int start = index > 0 ? content.LastIndexOf('\n', index - 1) + 1 : 0;
        int end = content.IndexOf('\n', index);
        return end < 0 ? content.Substring(start) : content.Substring(start, end - start);
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

    private async Task<IReadOnlyList<JObject>> WaitForRowsAsync(IdeCommandContext context, int timeoutMilliseconds, bool forceRefresh)
    {
        int timeout = timeoutMilliseconds > 0 ? timeoutMilliseconds : DefaultWaitTimeoutMilliseconds;
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeout);
        JObject[] lastRows = [];
        int? lastCount = null;
        int stableSamples = 0;
        int requiredStableSamples = StableSampleCount;

        if (forceRefresh)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);
            EnsureErrorListWindow(context.Dte);
        }

        while (DateTimeOffset.UtcNow < deadline)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(context.CancellationToken);

            IReadOnlyList<JObject>? rows = null;
            try
            {
                rows = ReadRows(context.Dte);
            }
            catch (InvalidOperationException)
            {
                if (forceRefresh)
                {
                    EnsureErrorListWindow(context.Dte);
                }
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
                // Wait for multiple stable reads even after IntelliSense reports ready,
                // because some Error List providers continue hydrating message rows after that point.
                if (stableSamples >= requiredStableSamples)
                {
                    return rows;
                }
            }

            await Task.Delay(PopulationPollIntervalMilliseconds, context.CancellationToken).ConfigureAwait(false);
        }

        return lastRows;
    }

    private IReadOnlyList<JObject> ReadRows(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        TryReadTableRows(out var tableRows);

        Window? window = TryGetErrorListWindow(dte);
        if (window?.Object is not ErrorList errorList)
        {
            if (tableRows.Count > 0)
                return tableRows;
            throw new InvalidOperationException("Error List window is not available.");
        }

        ErrorItems items = errorList.ErrorItems;
        List<JObject> dteRows = [];
        for (int i = 1; i <= items.Count; i++)
        {
            ErrorItem errorItem = items.Item(i);
            string severity = MapSeverity(errorItem.ErrorLevel);
            string description = errorItem.Description ?? string.Empty;
            string project = errorItem.Project ?? string.Empty;
            string file = errorItem.FileName ?? string.Empty;
            int line = errorItem.Line;
            int column = errorItem.Column;
            NormalizeBuildOutputLocation(ref file, ref line, ref column);
            string code = InferCode(description);
            dteRows.Add(new()
            {
                [SeverityKey] = severity,
                ["code"] = code,
                ["codeFamily"] = InferCodeFamily(code),
                ["tool"] = InferTool(code, description),
                ["message"] = description,
                ["project"] = project,
                ["file"] = file,
                ["line"] = line,
                ["column"] = column,
                ["symbols"] = new JArray(ExtractSymbols(description)),
            });
        }

        // Table rows are preferred (richer data); merge DTE rows to fill gaps
        // such as language-service diagnostics on Miscellaneous Files.
        if (tableRows.Count == 0)
            return dteRows;

        return MergeRows(tableRows, dteRows);
    }

    private bool TryReadTableRows(out IReadOnlyList<JObject> rows)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        rows = [];
        ITableManagerProvider? tableManagerProvider = GetTableManagerProvider();
        if (tableManagerProvider is null)
        {
            return false;
        }

        ITableManager tableManager = tableManagerProvider.GetTableManager(StandardTables.ErrorsTable);
        if (tableManager.Sources.Count == 0)
        {
            return false;
        }

        using ErrorTableCollector collector = new();
        List<IDisposable> subscriptions = [];
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
        string message = GetTableString(tryGetValue, StandardTableKeyNames.Text, StandardTableKeyNames.FullText);
        string project = GetTableString(tryGetValue, StandardTableKeyNames.ProjectName);
        string file = GetTableString(tryGetValue, StandardTableKeyNames.Path, StandardTableKeyNames.DocumentName);
        int line = GetTableCoordinate(tryGetValue, StandardTableKeyNames.Line);
        int column = GetTableCoordinate(tryGetValue, StandardTableKeyNames.Column);
        NormalizeBuildOutputLocation(ref file, ref line, ref column);
        string code = GetTableString(tryGetValue, StandardTableKeyNames.ErrorCode, StandardTableKeyNames.ErrorCodeToolTip);
        if (string.IsNullOrWhiteSpace(code))
        {
            code = InferCode(message);
        }

        return new JObject
        {
            [SeverityKey] = MapTableSeverity(tryGetValue),
            [CodeKey] = code,
            [ToolKey] = GetTableString(tryGetValue, StandardTableKeyNames.BuildTool),
            [MessageKey] = message,
            [ProjectKey] = project,
            [FileKey] = file,
            [LineKey] = line,
            [GuidanceKey] = GetTableString(tryGetValue, GuidanceKey),
            [SuggestedActionKey] = GetTableString(tryGetValue, SuggestedActionKey),
            [LlmFixPromptKey] = GetTableString(tryGetValue, LlmFixPromptKey),
            [AuthorityKey] = GetTableString(tryGetValue, AuthorityKey),
        };
    }

    private static string GetTableString(TableValueReader tryGetValue, params string[] keyNames)
    {
        foreach (var keyName in keyNames)
        {
            if (tryGetValue(keyName, out var content) && content is not null)
            {
                string value = content.ToString();
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

}
