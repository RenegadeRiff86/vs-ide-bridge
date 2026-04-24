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
using System.Threading;
using System.Threading.Tasks;
using VsIdeBridge.Infrastructure;
using VsIdeBridge.Diagnostics;
using static VsIdeBridge.Diagnostics.ErrorListConstants;

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

    public ErrorListQuery WithoutMax()
    {
        return new ErrorListQuery
        {
            Severity = Severity,
            Code = Code,
            Project = Project,
            Path = Path,
            Text = Text,
            GroupBy = GroupBy,
            Max = null,
        };
    }

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

internal sealed partial class ErrorListService
{
    private sealed class ErrorTableCollector : ITableDataSink, IDisposable
    {
        private readonly object _gate = new();
        private readonly List<ITableEntry> _entries = [];
        private readonly List<ITableEntriesSnapshot> _snapshots = [];
        private readonly List<ITableEntriesSnapshotFactory> _factories = [];
        private DateTimeOffset _lastMutationUtc = DateTimeOffset.UtcNow;
        private TaskCompletionSource<bool> _changeSignal = CreateChangeSignal();

        public bool HasData
        {
            get
            {
                lock (_gate)
                {
                    return _entries.Count > 0 || _snapshots.Count > 0 || _factories.Count > 0;
                }
            }
        }

        public bool IsStable { get; set; } = true;

        public IReadOnlyList<JObject> GetRows()
        {
            List<ITableEntry> entries;
            List<ITableEntriesSnapshot> snapshots;
            List<ITableEntriesSnapshotFactory> factories;
            lock (_gate)
            {
                entries = [.. _entries];
                snapshots = [.. _snapshots];
                factories = [.. _factories];
            }

            List<JObject> rows = [];

            foreach (ITableEntry entry in entries)
            {
                rows.Add(CreateRowFromTableEntry(entry));
            }

            foreach (ITableEntriesSnapshot snapshot in snapshots)
            {
                AddSnapshotRows(rows, snapshot);
            }

            foreach (ITableEntriesSnapshotFactory factory in factories)
            {
                AddSnapshotRows(rows, factory.GetCurrentSnapshot());
            }

            return [.. rows
                .GroupBy(CreateDiagnosticIdentity, StringComparer.OrdinalIgnoreCase)
                .Select(SelectPreferredDiagnosticRow)];
        }

        public async Task WaitForStabilityAsync(TimeSpan quietPeriod, TimeSpan timeout, CancellationToken cancellationToken)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Task changeTask;
                TimeSpan remainingQuiet;
                lock (_gate)
                {
                    remainingQuiet = quietPeriod - (DateTimeOffset.UtcNow - _lastMutationUtc);
                    if (remainingQuiet <= TimeSpan.Zero)
                    {
                        return;
                    }

                    changeTask = _changeSignal.Task;
                }

                TimeSpan remainingTimeout = deadline - DateTimeOffset.UtcNow;
                if (remainingTimeout <= TimeSpan.Zero)
                {
                    return;
                }

                TimeSpan waitDuration = remainingQuiet < remainingTimeout ? remainingQuiet : remainingTimeout;
                Task completed = await Task.WhenAny(changeTask, Task.Delay(waitDuration, cancellationToken)).ConfigureAwait(false);
                if (completed != changeTask)
                {
                    return;
                }
            }
        }

        public void AddEntries(IReadOnlyList<ITableEntry> newEntries, bool removeAllEntries)
        {
            lock (_gate)
            {
                if (removeAllEntries)
                {
                    _entries.Clear();
                }

                _entries.AddRange(newEntries);
            }

            RecordMutation();
        }

        public void RemoveEntries(IReadOnlyList<ITableEntry> oldEntries)
        {
            lock (_gate)
            {
                foreach (ITableEntry entry in oldEntries)
                {
                    _entries.Remove(entry);
                }
            }

            RecordMutation();
        }

        public void ReplaceEntries(IReadOnlyList<ITableEntry> oldEntries, IReadOnlyList<ITableEntry> newEntries)
        {
            RemoveEntries(oldEntries);
            AddEntries(newEntries, removeAllEntries: false);
        }

        public void RemoveAllEntries()
        {
            lock (_gate)
            {
                _entries.Clear();
            }

            RecordMutation();
        }

        public void AddSnapshot(ITableEntriesSnapshot newSnapshot, bool removeAllSnapshots)
        {
            lock (_gate)
            {
                if (removeAllSnapshots)
                {
                    _snapshots.Clear();
                }

                _snapshots.Add(newSnapshot);
            }

            RecordMutation();
        }

        public void RemoveSnapshot(ITableEntriesSnapshot oldSnapshot)
        {
            lock (_gate)
            {
                _snapshots.Remove(oldSnapshot);
            }

            RecordMutation();
        }

        public void ReplaceSnapshot(ITableEntriesSnapshot oldSnapshot, ITableEntriesSnapshot newSnapshot)
        {
            RemoveSnapshot(oldSnapshot);
            AddSnapshot(newSnapshot, removeAllSnapshots: false);
        }

        public void AddFactory(ITableEntriesSnapshotFactory newFactory, bool removeAllFactories)
        {
            lock (_gate)
            {
                if (removeAllFactories)
                {
                    _factories.Clear();
                }

                _factories.Add(newFactory);
            }

            RecordMutation();
        }

        public void RemoveFactory(ITableEntriesSnapshotFactory oldFactory)
        {
            lock (_gate)
            {
                _factories.Remove(oldFactory);
            }

            RecordMutation();
        }

        public void ReplaceFactory(ITableEntriesSnapshotFactory oldFactory, ITableEntriesSnapshotFactory newFactory)
        {
            RemoveFactory(oldFactory);
            AddFactory(newFactory, removeAllFactories: false);
        }

        public void FactorySnapshotChanged(ITableEntriesSnapshotFactory? factory)
        {
            if (factory is not null)
            {
                RecordMutation();
            }
        }

        public void RemoveAllFactories()
        {
            lock (_gate)
            {
                _factories.Clear();
            }

            RecordMutation();
        }

        public void RemoveAllSnapshots()
        {
            lock (_gate)
            {
                _snapshots.Clear();
            }

            RecordMutation();
        }

        public void Dispose()
        {
        }

        private static void AddSnapshotRows(List<JObject> rows, ITableEntriesSnapshot snapshot)
        {
            for (int index = 0; index < snapshot.Count; index++)
            {
                rows.Add(CreateRowFromTableSnapshot(snapshot, index));
            }
        }

        private static TaskCompletionSource<bool> CreateChangeSignal()
        {
            return new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private void RecordMutation()
        {
            TaskCompletionSource<bool> signal;
            lock (_gate)
            {
                _lastMutationUtc = DateTimeOffset.UtcNow;
                signal = _changeSignal;
                _changeSignal = CreateChangeSignal();
            }

            signal.TrySetResult(true);
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
            BestPracticeTableEntry[] entries = [..rows.Select(BestPracticeTableEntry.FromRow)];
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

            return typedSnapshot._entryIndexes.TryGetValue(_entries[currentIndex].StableKey, out int newIndex)
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

            BestPracticeTableEntry entry = _entries[index];
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
                    content = Path.GetFileName(entry.Location.File);
                    return true;
                case StandardTableKeyNames.Path:
                    content = entry.Location.File;
                    return true;
                case StandardTableKeyNames.Line:
                    content = Math.Max(0, entry.Location.Line - 1);
                    return true;
                case StandardTableKeyNames.Column:
                    content = Math.Max(0, entry.Location.Column - 1);
                    return true;
                case StandardTableKeyNames.ProjectName:
                    content = entry.Location.Project;
                    return true;
                case StandardTableKeyNames.BuildTool:
                    content = entry.Attribution.Tool;
                    return true;
                case StandardTableKeyNames.ErrorSource:
                    content = Microsoft.VisualStudio.Shell.TableManager.ErrorSource.Build;
                    return true;
                case StandardTableKeyNames.HelpKeyword:
                    content = string.IsNullOrWhiteSpace(entry.Code) ? BestPracticeCategory : entry.Code;
                    return true;
                case StandardTableKeyNames.HelpLink:
                    content = entry.Remediation.HelpUri;
                    return true;
                case GuidanceKey:
                    content = entry.Remediation.Guidance;
                    return true;
                case SuggestedActionKey:
                    content = entry.Remediation.SuggestedAction;
                    return true;
                case LlmFixPromptKey:
                    content = entry.Remediation.LlmFixPrompt;
                    return true;
                case AuthorityKey:
                    content = entry.Attribution.Authority;
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

    private sealed class BestPracticeTableLocation(string file, int line, int column, string project)
    {
        public string File { get; } = file;
        public int Line { get; } = line;
        public int Column { get; } = column;
        public string Project { get; } = project;
    }

    private sealed class BestPracticeTableRemediation(string helpUri, string guidance, string suggestedAction, string llmFixPrompt)
    {
        public string HelpUri { get; } = helpUri;
        public string Guidance { get; } = guidance;
        public string SuggestedAction { get; } = suggestedAction;
        public string LlmFixPrompt { get; } = llmFixPrompt;
    }

    private sealed class BestPracticeTableAttribution(string tool, string authority)
    {
        public string Tool { get; } = tool;
        public string Authority { get; } = authority;
    }

    private sealed class BestPracticeTableEntry(string severity, string code, string message, BestPracticeTableLocation location, BestPracticeTableRemediation remediation, BestPracticeTableAttribution attribution)
    {
        public string Severity { get; } = severity;
        public string Code { get; } = code;
        public string Message { get; } = message;
        public BestPracticeTableLocation Location { get; } = location;
        public BestPracticeTableRemediation Remediation { get; } = remediation;
        public BestPracticeTableAttribution Attribution { get; } = attribution;

        public string StableKey => string.Join("|", Severity, Code, Location.File, Location.Line.ToString(CultureInfo.InvariantCulture), Location.Column.ToString(CultureInfo.InvariantCulture), Message);

        public static BestPracticeTableEntry FromRow(JObject row)
        {
            return new BestPracticeTableEntry(
                string.IsNullOrEmpty(GetRowString(row, SeverityKey)) ? WarningSeverity : GetRowString(row, SeverityKey),
                GetRowString(row, CodeKey),
                GetRowString(row, MessageKey),
                new BestPracticeTableLocation(
                    GetRowString(row, FileKey),
                    Math.Max(1, GetNullableRowInt(row, LineKey) ?? 1),
                    Math.Max(1, GetNullableRowInt(row, ColumnKey) ?? 1),
                    GetRowString(row, ProjectKey)),
                new BestPracticeTableRemediation(
                    GetRowString(row, HelpUriKey),
                    GetRowString(row, GuidanceKey),
                    GetRowString(row, SuggestedActionKey),
                    GetRowString(row, LlmFixPromptKey)),
                new BestPracticeTableAttribution(
                    string.IsNullOrEmpty(GetRowString(row, ToolKey)) ? BestPracticeCategory : GetRowString(row, ToolKey),
                    GetRowString(row, AuthorityKey)));
        }
    }
}
