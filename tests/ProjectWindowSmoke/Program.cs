using Avalonia;
using Avalonia.Controls;
using CaptionExpress;

AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
var window = new MainWindow();
foreach (string name in new[]
{
    "ProjectCombo", "NewProjectNameBox", "NewProjectMediaPolicyCombo", "NewProjectVideoFormatCombo",
    "VideoFormatText", "BatchVideoFormatText", "ProjectSummaryText",
    "BucketCombo", "FrameRateCombo", "BatchTimingText", "FinalDatasetDescriptionText",
    "FirstFrameImage", "DatasetFirstFrameImage", "DatasetAudioWarningText", "DatasetFilterCombo",
    "DatasetSaveButton", "DatasetReviewButton", "DatasetPendingButton", "DatasetExcludeButton"
})
    if (window.FindControl<Control>(name) is null)
        throw new InvalidOperationException($"Missing project workflow control: {name}");

var mediaPolicy = window.FindControl<ComboBox>("NewProjectMediaPolicyCombo")!;
if (mediaPolicy.SelectedIndex != 0 || mediaPolicy.ItemsSource?.Cast<object>().Count() != 2)
    throw new InvalidOperationException("New projects must default to joint video and audio with a video-only option.");

var projectFormat = window.FindControl<ComboBox>("NewProjectVideoFormatCombo")!;
if (projectFormat.SelectedIndex != 0 || projectFormat.ItemsSource?.Cast<object>().Count() != 5)
    throw new InvalidOperationException("New projects must offer the supported video formats and default to H.264 MP4.");

var reviewList = window.FindControl<ListBox>("DatasetPairsList")!;
if (!reviewList.SelectionMode.HasFlag(SelectionMode.Multiple))
    throw new InvalidOperationException("The dataset review list must support Ctrl/Shift multi-selection.");

// The review controls must stay on screen at the advertised minimum window size.
window.Width = 1000;
window.Height = 700;
window.FindControl<TabControl>("WorkflowTabs")!.SelectedIndex = 2;
window.Show();
window.Measure(new Size(1000, 700));
window.Arrange(new Rect(0, 0, 1000, 700));
foreach (string name in new[] { "DatasetPlayButton", "DatasetSaveButton", "DatasetReviewButton", "DatasetExcludeButton" })
{
    var button = window.FindControl<Button>(name)!;
    Point? position = button.TranslatePoint(new Point(0, 0), window);
    if (position is null || button.Bounds.Height <= 0 || position.Value.Y + button.Bounds.Height > window.Bounds.Height)
        throw new InvalidOperationException($"Review control {name} is clipped at the minimum window height.");
}
window.Close();
var singleWindow = new MainWindow { Width = 1000, Height = 700 };
singleWindow.Show();
singleWindow.Measure(new Size(1000, 700));
singleWindow.Arrange(new Rect(0, 0, 1000, 700));
foreach (string name in new[] { "PlayButton", "SetStartButton", "SaveButton" })
{
    var button = singleWindow.FindControl<Button>(name)!;
    Point? position = button.TranslatePoint(new Point(0, 0), singleWindow);
    if (position is null || button.Bounds.Height <= 0 || position.Value.Y + button.Bounds.Height > singleWindow.Bounds.Height)
        throw new InvalidOperationException($"One-clip control {name} is clipped at the minimum window height.");
}
var range = singleWindow.FindControl<Border>("RangePanel")!;
var statusStrip = singleWindow.FindControl<Border>("StatusStrip")!;
Point? rangePosition = range.TranslatePoint(new Point(0, 0), singleWindow);
Point? statusPosition = statusStrip.TranslatePoint(new Point(0, 0), singleWindow);
if (rangePosition is null || statusPosition is null || rangePosition.Value.Y + range.Bounds.Height > statusPosition.Value.Y)
    throw new InvalidOperationException($"The video range panel overlaps the pinned status strip: " +
        $"range bottom {rangePosition?.Y + range.Bounds.Height:0.##}, status top {statusPosition?.Y:0.##}.");
singleWindow.Close();

Console.WriteLine("Project window smoke test passed: export, preview, and multi-select review controls load.");
