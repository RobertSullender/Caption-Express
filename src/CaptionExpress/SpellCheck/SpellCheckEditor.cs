using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace CaptionExpress.SpellCheck;

/// <summary>A prose editor with offline spelling marks and a correction menu.</summary>
public sealed class SpellCheckEditor : TextEditor
{
    // Avalonia styles templated controls by their style key. Without the base key,
    // the AvaloniaEdit template is not applied and the editor appears blank.
    protected override Type StyleKeyOverride => typeof(TextEditor);

    private readonly SpellingRenderer _renderer = new();
    private readonly ContextMenu _contextMenu = new();
    private CancellationTokenSource? _scanCancellation;
    private IReadOnlyList<SpellingIssue> _issues = [];

    public SpellCheckEditor()
    {
        // AvaloniaEdit's default text color is dark even when the host app uses
        // Avalonia's dark Fluent theme. Keep editable text and the caret visible.
        Foreground = Brushes.White;
        Background = new SolidColorBrush(Color.Parse("#141B24"));
        TextArea.Caret.CaretBrush = Brushes.White;
        WordWrap = true;
        TextArea.RightClickMovesCaret = true;
        TextArea.TextView.BackgroundRenderers.Add(_renderer);
        _contextMenu.Opening += (_, _) => BuildContextMenu();
        TextArea.ContextMenu = _contextMenu;
        TextChanged += (_, _) => ScheduleCheck();
        if (SpellDictionary.Instance.Error is { } error)
            ToolTip.SetTip(this, error);
        ScheduleCheck();
    }

    protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        SpellDictionary.Instance.PersonalWordsChanged += ScheduleCheck;
        ScheduleCheck();
    }

    protected override void OnDetachedFromLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        SpellDictionary.Instance.PersonalWordsChanged -= ScheduleCheck;
        _scanCancellation?.Cancel();
        base.OnDetachedFromLogicalTree(e);
    }

    private void ScheduleCheck()
    {
        _scanCancellation?.Cancel();
        _scanCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        var snapshot = Text;
        _issues = [];
        _renderer.SetIssues(TextArea.TextView, _issues);
        _ = CheckLaterAsync(snapshot, cancellation.Token);
    }

    private async Task CheckLaterAsync(string snapshot, CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(220, cancellation);
            var found = await Task.Run(() => SpellDictionary.Instance.FindIssues(snapshot), cancellation);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cancellation.IsCancellationRequested || !string.Equals(Text, snapshot, StringComparison.Ordinal))
                    return;
                _issues = found;
                _renderer.SetIssues(TextArea.TextView, found);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => ToolTip.SetTip(this, $"Spell checking unavailable: {ex.Message}"));
        }
    }

    private void BuildContextMenu()
    {
        var entries = new List<object>();
        var offset = CaretOffset;
        SpellingIssue? selected = null;
        foreach (var issue in _issues)
        {
            if (offset >= issue.Start && offset < issue.Start + issue.Length)
            {
                selected = issue;
                break;
            }
        }

        if (selected is { } misspelling)
        {
            foreach (var suggestion in SpellDictionary.Instance.Suggest(misspelling.Word))
            {
                var replacement = suggestion;
                var item = new MenuItem { Header = replacement };
                item.Click += (_, _) => ReplaceIssue(misspelling, replacement);
                entries.Add(item);
            }
            if (entries.Count == 0)
                entries.Add(new MenuItem { Header = "No suggestions", IsEnabled = false });
            var addWord = new MenuItem { Header = $"Add ‘{misspelling.Word}’ to dictionary" };
            addWord.Click += (_, _) =>
            {
                if (!SpellDictionary.Instance.AddWord(misspelling.Word, out var error) && error is not null)
                    ToolTip.SetTip(this, error);
            };
            entries.Add(addWord);
            entries.Add(new Separator());
        }
        else if (SpellDictionary.Instance.Error is { } error)
        {
            entries.Add(new MenuItem { Header = error, IsEnabled = false });
            entries.Add(new Separator());
        }

        entries.Add(MakeAction("Undo", () => Undo(), CanUndo && !IsReadOnly));
        entries.Add(MakeAction("Redo", () => Redo(), CanRedo && !IsReadOnly));
        entries.Add(new Separator());
        entries.Add(MakeAction("Cut", Cut, CanCut && !IsReadOnly));
        entries.Add(MakeAction("Copy", Copy, CanCopy));
        entries.Add(MakeAction("Paste", Paste, CanPaste && !IsReadOnly));
        entries.Add(new Separator());
        entries.Add(MakeAction("Select all", SelectAll, CanSelectAll));
        _contextMenu.ItemsSource = entries;
    }

    private static MenuItem MakeAction(string label, Action action, bool enabled)
    {
        var item = new MenuItem { Header = label, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    private void ReplaceIssue(SpellingIssue issue, string replacement)
    {
        // Verify the text still contains this word before changing a delayed menu choice.
        if (Document is null || issue.Start < 0 || issue.Start + issue.Length > Document.TextLength ||
            !string.Equals(Document.GetText(issue.Start, issue.Length), issue.Word, StringComparison.Ordinal))
            return;
        Document.Replace(issue.Start, issue.Length, replacement);
        CaretOffset = issue.Start + replacement.Length;
        Focus();
    }

    private sealed class Marker : TextSegment;

    private sealed class SpellingRenderer : IBackgroundRenderer
    {
        private static readonly Pen UnderlinePen = new(Brushes.IndianRed, 1.6);
        private readonly TextSegmentCollection<Marker> _markers = [];

        public KnownLayer Layer => KnownLayer.Background;

        public void SetIssues(TextView view, IReadOnlyList<SpellingIssue> issues)
        {
            _markers.Clear();
            foreach (var issue in issues)
                _markers.Add(new Marker { StartOffset = issue.Start, Length = issue.Length });
            view.InvalidateLayer(Layer);
        }

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (_markers.Count == 0 || textView.VisualLines.Count == 0) return;
            var visibleStart = textView.VisualLines[0].FirstDocumentLine.Offset;
            var visibleEnd = textView.VisualLines[^1].LastDocumentLine.EndOffset;
            foreach (var marker in _markers.FindOverlappingSegments(visibleStart, visibleEnd - visibleStart))
            {
                foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, marker))
                {
                    if (rect.Width <= 1) continue;
                    var geometry = new StreamGeometry();
                    using (var context = geometry.Open())
                    {
                        var y = rect.Bottom - 2;
                        context.BeginFigure(new Point(rect.Left, y), false);
                        for (var x = rect.Left + 2.5; x < rect.Right; x += 2.5)
                            context.LineTo(new Point(Math.Min(x, rect.Right), y + ((int)((x - rect.Left) / 2.5) % 2 == 0 ? -1.8 : 1.8)));
                        context.LineTo(new Point(rect.Right, y));
                    }
                    drawingContext.DrawGeometry(null, UnderlinePen, geometry);
                }
            }
        }
    }
}
