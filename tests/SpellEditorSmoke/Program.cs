using Avalonia;
using Avalonia.Media;
using AvaloniaEdit;
using CaptionExpress;
using CaptionExpress.SpellCheck;

AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
var editor = new SpellCheckEditor();
Require(editor.StyleKey == typeof(TextEditor), "derived editor uses AvaloniaEdit's control template");
Require(editor.Foreground == Brushes.White, "prose is readable in the dark theme");
Require(editor.TextArea.Caret.CaretBrush == Brushes.White, "caret is readable in the dark theme");
editor.Text = "A girll crawls through a tunnel.";
Require(editor.Text == "A girll crawls through a tunnel.", "editor accepts and retains editable text");
editor.Document.Insert(editor.Document.TextLength, " The soundtrack hums.");
Require(editor.Text.EndsWith(" The soundtrack hums.", StringComparison.Ordinal),
    "document edits update the visible caption text");
Console.WriteLine("SpellEditorSmoke passed.");

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
