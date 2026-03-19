using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;

namespace VsIdeBridge.Services;

[Export(typeof(EditorFormatDefinition))]
[Name("VsIdeBridgeChangedLine")]
[UserVisible(true)]
internal sealed class BridgeChangedLineFormatDefinition : MarkerFormatDefinition
{
    public BridgeChangedLineFormatDefinition()
    {
        BackgroundColor = System.Windows.Media.Color.FromRgb(0x2B, 0x4A, 0x2A);
        ForegroundColor = System.Windows.Media.Colors.White;
        DisplayName = "VS IDE Bridge Changed Lines";
        ZOrder = 6;
    }
}

[Export(typeof(EditorFormatDefinition))]
[Name("VsIdeBridgeDeletedLine")]
[UserVisible(true)]
internal sealed class BridgeDeletedLineFormatDefinition : MarkerFormatDefinition
{
    public BridgeDeletedLineFormatDefinition()
    {
        BackgroundColor = System.Windows.Media.Color.FromRgb(0x5A, 0x22, 0x22);
        ForegroundColor = System.Windows.Media.Colors.White;
        DisplayName = "VS IDE Bridge Deleted Line Markers";
        ZOrder = 7;
    }
}

[Export(typeof(IViewTaggerProvider))]
[ContentType("text")]
[TagType(typeof(TextMarkerTag))]
internal sealed class BridgeEditHighlightTaggerProvider : IViewTaggerProvider
{
    public ITagger<T>? CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag
    {
        if (textView.TextBuffer != buffer)
        {
            return null;
        }

        return textView.Properties.GetOrCreateSingletonProperty(
            () => new BridgeEditHighlightTagger(textView, buffer)) as ITagger<T>;
    }

    private sealed class BridgeEditHighlightTagger : ITagger<TextMarkerTag>
    {
        private readonly ITextView _textView;
        private readonly ITextBuffer _buffer;

        public BridgeEditHighlightTagger(ITextView textView, ITextBuffer buffer)
        {
            _textView = textView;
            _buffer = buffer;
            BridgeEditHighlightService.Instance.HighlightsChanged += OnHighlightsChanged;
            _textView.Closed += OnViewClosed;
        }

        public event EventHandler<SnapshotSpanEventArgs>? TagsChanged;

        public IEnumerable<ITagSpan<TextMarkerTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (spans.Count == 0)
            {
                yield break;
            }

            var snapshot = spans[0].Snapshot;
            foreach (var (span, markerType) in BridgeEditHighlightService.Instance.GetHighlights(snapshot))
            {
                if (!spans.IntersectsWith(span))
                    continue;
                yield return new TagSpan<TextMarkerTag>(span, new TextMarkerTag(markerType));
            }
        }

        private void OnHighlightsChanged(object? sender, SnapshotSpanEventArgs e)
        {
            if (e.Span.Snapshot.TextBuffer == _buffer)
            {
                TagsChanged?.Invoke(this, e);
            }
        }

        private void OnViewClosed(object? sender, EventArgs e)
        {
            _textView.Closed -= OnViewClosed;
            BridgeEditHighlightService.Instance.HighlightsChanged -= OnHighlightsChanged;
        }
    }
}
