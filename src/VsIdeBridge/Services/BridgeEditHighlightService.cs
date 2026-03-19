using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace VsIdeBridge.Services;

internal sealed class BridgeEditHighlightService
{
    private const int HighlightExpirationMinutes = 5;

    private sealed class BufferHighlights
    {
        public List<ITrackingSpan> AddedOrModified { get; } = [];

        public List<ITrackingSpan> DeletedMarkers { get; } = [];

        public DateTimeOffset ExpiresAtUtc { get; set; }
    }

    public static BridgeEditHighlightService Instance { get; } = new();

    private readonly ConcurrentDictionary<ITextBuffer, BufferHighlights> _highlights = new();

    public event EventHandler<SnapshotSpanEventArgs>? HighlightsChanged;

    public void ApplyHighlights(IWpfTextView view, IReadOnlyCollection<(int StartLine, int EndLine)> changedRanges, IReadOnlyCollection<int> deletedLines)
    {
        var snapshot = view.TextSnapshot;
        var state = new BufferHighlights
        {
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(HighlightExpirationMinutes),
        };

        foreach (var (StartLine, EndLine) in changedRanges)
        {
            var startLine = Math.Max(1, StartLine);
            var endLine = Math.Max(startLine, EndLine);
            if (startLine > snapshot.LineCount)
            {
                continue;
            }

            var snapshotStart = snapshot.GetLineFromLineNumber(startLine - 1).Start;
            var lastLine = snapshot.GetLineFromLineNumber(Math.Min(snapshot.LineCount, endLine) - 1);
            var span = new SnapshotSpan(snapshotStart, lastLine.EndIncludingLineBreak);
            state.AddedOrModified.Add(snapshot.CreateTrackingSpan(span, SpanTrackingMode.EdgeExclusive));
        }

        foreach (var lineNumber in deletedLines.Distinct().Where(value => value > 0))
        {
            var lineIndex = Math.Min(snapshot.LineCount - 1, Math.Max(0, lineNumber - 1));
            var line = snapshot.GetLineFromLineNumber(lineIndex);
            state.DeletedMarkers.Add(snapshot.CreateTrackingSpan(new SnapshotSpan(line.Start, 0), SpanTrackingMode.EdgeNegative));
        }

        _highlights.AddOrUpdate(snapshot.TextBuffer, state, (_, _) => state);
        HighlightsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
    }

    public IReadOnlyList<(SnapshotSpan Span, string MarkerType)> GetHighlights(ITextSnapshot snapshot)
    {
        if (!_highlights.TryGetValue(snapshot.TextBuffer, out var state))
        {
            return [];
        }

        if (DateTimeOffset.UtcNow >= state.ExpiresAtUtc)
        {
            _highlights.TryRemove(snapshot.TextBuffer, out _);
            return [];
        }

        var highlightSpans = new List<(SnapshotSpan, string)>();
        highlightSpans.AddRange(state.AddedOrModified.Select(span => (span.GetSpan(snapshot), "VsIdeBridgeChangedLine")));
        highlightSpans.AddRange(state.DeletedMarkers.Select(span => (span.GetSpan(snapshot), "VsIdeBridgeDeletedLine")));
        return highlightSpans;
    }
}
