using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CaptionExpress.Caption;
using CaptionExpress.Dataset;
using CaptionExpress.Media;
using CaptionExpress.SpellCheck;
using LibVLCSharp.Avalonia;
using LibVLCSharp.Shared;

namespace CaptionExpress;

public partial class MainWindow
{
    private enum DatasetFilter { NeedsReview, Reviewed, Excluded, All }

    private sealed record DatasetFilterOption(DatasetFilter Filter, string Label)
    {
        public override string ToString() => Label;
    }

    private static readonly DatasetFilterOption[] DatasetFilters =
    [
        new(DatasetFilter.NeedsReview, "Needs review"),
        new(DatasetFilter.Reviewed, "Reviewed"),
        new(DatasetFilter.Excluded, "Excluded"),
        new(DatasetFilter.All, "All pairs")
    ];

    private sealed record DatasetListEntry(DatasetPair Pair)
    {
        public override string ToString()
        {
            string state = Pair.ReviewStatus switch
            {
                DatasetReviewStatus.Reviewed => "Reviewed",
                DatasetReviewStatus.Excluded => "Excluded",
                _ => Pair.CaptionPath is null ? "Needs caption" : "Needs review"
            };
            return $"{Path.GetFileName(Pair.MediaPath)}  ·  {state}";
        }
    }

    private TextBlock _datasetWorkingFolder = null!;
    private Button _refreshDataset = null!;
    private ComboBox _datasetFilterCombo = null!;
    private TextBlock _datasetListSummary = null!;
    private ListBox _datasetPairsList = null!;
    private CaptionVideoView _datasetVideoView = null!;
    private Image _datasetFirstFrameImage = null!;
    private Button _datasetPlayButton = null!;
    private TextBlock _datasetSelectedFile = null!;
    private TextBlock _datasetAudioWarning = null!;
    private TextBlock _datasetSelectionText = null!;
    private SpellCheckEditor _datasetCaption = null!;
    private Button _datasetSaveButton = null!;
    private Button _datasetDiscardEdits = null!;
    private Button _datasetReviewButton = null!;
    private Button _datasetPendingButton = null!;
    private Button _datasetExcludeButton = null!;
    private TextBox _finalDatasetPath = null!;
    private Button _chooseFinalDatasetParent = null!;
    private Button _finalizeDatasetButton = null!;
    private TextBlock _datasetStatus = null!;
    private MediaPlayer? _datasetPlayer;
    private Bitmap? _datasetFrame;
    private CancellationTokenSource? _datasetPreviewCancellation;
    private IReadOnlyList<DatasetPair> _datasetAllPairs = [];
    private DatasetFilter _datasetFilter = DatasetFilter.NeedsReview;
    private HashSet<string> _datasetSelectedPaths = new(StringComparer.Ordinal);
    private DatasetListEntry? _datasetSelected;
    private string _datasetSavedCaption = "";
    private bool _datasetCaptionDirty;
    private bool _settingDatasetCaption;
    private bool _settingDatasetSelection;
    private bool _settingDatasetFilter;
    private bool _datasetLoading;
    private bool _datasetStartingPlayback;
    private DatasetListEntry? _datasetPointerTarget;
    private int _datasetLoadVersion;
    private int _datasetSelectionVersion;
    private string? _datasetListedFolder;

    private void InitializeDatasetUi()
    {
        _datasetWorkingFolder = Get<TextBlock>("DatasetWorkingFolderText");
        _refreshDataset = Get<Button>("RefreshDatasetButton");
        _datasetFilterCombo = Get<ComboBox>("DatasetFilterCombo");
        _datasetListSummary = Get<TextBlock>("DatasetListSummaryText");
        _datasetPairsList = Get<ListBox>("DatasetPairsList");
        _datasetPairsList.AddHandler(InputElement.PointerPressedEvent, DatasetPairPointerPressed,
            RoutingStrategies.Tunnel);
        _datasetPairsList.AddHandler(InputElement.PointerReleasedEvent,
            (_, _) => _datasetPointerTarget = null, RoutingStrategies.Bubble);
        _datasetPairsList.AddHandler(InputElement.KeyDownEvent,
            (_, _) => _datasetPointerTarget = null, RoutingStrategies.Tunnel);
        _datasetVideoView = Get<CaptionVideoView>("DatasetVideoView");
        _datasetFirstFrameImage = Get<Image>("DatasetFirstFrameImage");
        _datasetPlayButton = Get<Button>("DatasetPlayButton");
        _datasetSelectedFile = Get<TextBlock>("DatasetSelectedFileText");
        _datasetAudioWarning = Get<TextBlock>("DatasetAudioWarningText");
        _datasetSelectionText = Get<TextBlock>("DatasetSelectionText");
        _datasetCaption = Get<SpellCheckEditor>("DatasetCaptionBox");
        _datasetSaveButton = Get<Button>("DatasetSaveButton");
        _datasetDiscardEdits = Get<Button>("DatasetDiscardEditsButton");
        _datasetReviewButton = Get<Button>("DatasetReviewButton");
        _datasetPendingButton = Get<Button>("DatasetPendingButton");
        _datasetExcludeButton = Get<Button>("DatasetExcludeButton");
        _finalDatasetPath = Get<TextBox>("FinalDatasetPathBox");
        _chooseFinalDatasetParent = Get<Button>("ChooseFinalDatasetParentButton");
        _finalizeDatasetButton = Get<Button>("FinalizeDatasetButton");
        _datasetStatus = Get<TextBlock>("DatasetStatusText");
        _settingDatasetFilter = true;
        _datasetFilterCombo.ItemsSource = DatasetFilters;
        _datasetFilterCombo.SelectedItem = DatasetFilters[0];
        _settingDatasetFilter = false;
        _datasetWorkingFolder.Text = "Working exports: " + _exportDirectory.Text;
        _exportDirectory.TextChanged += (_, _) =>
        {
            _datasetWorkingFolder.Text = "Working exports: " + (_exportDirectory.Text?.Trim() ?? "");
            if (_datasetListedFolder is not null && !IsCurrentDatasetFolder())
                _datasetStatus.Text = "Working exports changed. Save any open edit, then refresh pairs to view this folder.";
        };
        _finalDatasetPath.Text = DefaultFinalDatasetPath();
        _datasetCaption.TextChanged += (_, _) =>
        {
            if (!_settingDatasetCaption)
                _datasetCaptionDirty = !string.Equals(_datasetCaption.Text ?? "", _datasetSavedCaption,
                    StringComparison.Ordinal);
            UpdateDatasetButtons();
        };
    }

    private string DefaultFinalDatasetPath()
    {
        string projectName = _activeProject?.Name ?? "Select-a-project";
        string basePath = Path.Combine(_projectRoot, "finalized-datasets", projectName,
            "Dataset-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        string candidate = basePath;
        for (int suffix = 2; Directory.Exists(candidate) || File.Exists(candidate); suffix++)
            candidate = basePath + "-" + suffix;
        return candidate;
    }

    private DatasetCatalog CurrentCatalog()
    {
        string folder = _activeProject?.DirectoryPath ??
            throw new InvalidOperationException("Create or select a project first.");
        return new DatasetCatalog(folder);
    }

    private static DatasetCatalog CatalogForPair(string mediaPath) =>
        new(Path.GetDirectoryName(mediaPath) ?? throw new InvalidOperationException("The pair has no export folder."));

    private bool IsCurrentDatasetFolder()
    {
        string? current = _exportDirectory.Text?.Trim();
        return current is not null && current.Length > 0 && _datasetListedFolder is not null &&
               string.Equals(Path.GetFullPath(current), _datasetListedFolder,
                   OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private async void RefreshDatasetClicked(object? sender, RoutedEventArgs e)
    {
        if (_isBusy || _datasetLoading) return;
        try { await RefreshDatasetPairsAsync(); }
        catch (Exception error) { _datasetStatus.Text = "Could not refresh: " + error.Message; }
    }

    private async Task RefreshDatasetPairsAsync(CancellationToken cancellationToken = default,
        string? selectPath = null)
    {
        if (_datasetCaptionDirty)
            throw new InvalidOperationException("Save or discard the current dataset caption edits before refreshing.");
        string projectDirectory = _activeProject?.DirectoryPath ??
            throw new InvalidOperationException("Create or select a project first.");
        int version = ++_datasetLoadVersion;
        _datasetLoading = true;
        UpdateDatasetButtons();
        try
        {
            string? previous = selectPath ?? _datasetSelected?.Pair.MediaPath;
            var selectedPaths = new HashSet<string>(_datasetSelectedPaths, StringComparer.Ordinal);
            if (selectPath is not null) selectedPaths.Add(selectPath);
            var pairs = await new DatasetCatalog(projectDirectory).ListPairsAsync(cancellationToken);
            if (version != _datasetLoadVersion || _activeProject?.DirectoryPath != projectDirectory) return;
            _datasetListedFolder = projectDirectory;
            int previousIndex = (_datasetPairsList.ItemsSource as IEnumerable<DatasetListEntry>)?
                .Select((entry, index) => (entry, index))
                .FirstOrDefault(item => item.entry.Pair.MediaPath == previous).index ?? 0;
            _datasetAllPairs = pairs;
            await ApplyDatasetFilterAsync(selectedPaths, previous, previousIndex);
            if (version != _datasetLoadVersion || _activeProject?.DirectoryPath != projectDirectory) return;
            int reviewed = pairs.Count(pair => pair.HasVideo && pair.ReviewStatus == DatasetReviewStatus.Reviewed);
            int pending = pairs.Count(pair => pair.ReviewStatus == DatasetReviewStatus.Pending);
            int excluded = pairs.Count(pair => pair.ReviewStatus == DatasetReviewStatus.Excluded);
            _datasetListSummary.Text = $"{pairs.Count} total · {pending} need review · {reviewed} reviewed videos · {excluded} excluded";
            _datasetStatus.Text = $"Showing {_datasetFilterCombo.SelectedItem} pairs. {reviewed} reviewed video pairs are ready to finalize.";
        }
        finally
        {
            if (version == _datasetLoadVersion)
            {
                _settingDatasetSelection = false;
                _datasetLoading = false;
                UpdateDatasetButtons();
            }
        }
    }

    private async void DatasetFilterChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_settingDatasetFilter || _datasetLoading || _datasetFilterCombo is null) return;
        if (_datasetCaptionDirty)
        {
            _settingDatasetFilter = true;
            _datasetFilterCombo.SelectedItem = DatasetFilters.First(option => option.Filter == _datasetFilter);
            _settingDatasetFilter = false;
            _datasetStatus.Text = "Save or discard this caption before changing the review filter.";
            return;
        }
        if (_datasetFilterCombo.SelectedItem is not DatasetFilterOption choice) return;
        _datasetFilter = choice.Filter;
        _datasetLoading = true;
        UpdateDatasetButtons();
        try
        {
            await ApplyDatasetFilterAsync();
            _datasetStatus.Text = $"Showing {choice.Label.ToLowerInvariant()} pairs.";
        }
        catch (Exception error) { _datasetStatus.Text = "Could not change filter: " + error.Message; }
        finally
        {
            _datasetLoading = false;
            UpdateDatasetButtons();
        }
    }

    private static bool MatchesDatasetFilter(DatasetPair pair, DatasetFilter filter) => filter switch
    {
        DatasetFilter.NeedsReview => pair.ReviewStatus == DatasetReviewStatus.Pending,
        DatasetFilter.Reviewed => pair.ReviewStatus == DatasetReviewStatus.Reviewed,
        DatasetFilter.Excluded => pair.ReviewStatus == DatasetReviewStatus.Excluded,
        _ => true
    };

    private async Task ApplyDatasetFilterAsync(HashSet<string>? selectedPaths = null, string? activePath = null,
        int fallbackIndex = 0)
    {
        selectedPaths ??= _datasetSelectedPaths;
        activePath ??= _datasetSelected?.Pair.MediaPath;
        var entries = _datasetAllPairs.Where(pair => MatchesDatasetFilter(pair, _datasetFilter))
            .Select(pair => new DatasetListEntry(pair)).ToArray();
        DatasetListEntry? active;
        _settingDatasetSelection = true;
        try
        {
            _datasetPairsList.ItemsSource = entries;
            var selected = entries.Where(entry => selectedPaths.Contains(entry.Pair.MediaPath)).ToArray();
            if (selected.Length == 0 && entries.Length > 0)
                selected = [entries[Math.Clamp(fallbackIndex, 0, entries.Length - 1)]];
            if (_datasetPairsList.SelectedItems is { } selection)
            {
                selection.Clear();
                foreach (var entry in selected) selection.Add(entry);
            }
            _datasetSelectedPaths = selected.Select(entry => entry.Pair.MediaPath)
                .ToHashSet(StringComparer.Ordinal);
            active = selected.FirstOrDefault(entry => entry.Pair.MediaPath == activePath)
                     ?? selected.LastOrDefault();
        }
        finally
        {
            _settingDatasetSelection = false;
            UpdateDatasetButtons();
        }
        if (active is null) ClearDatasetSelection();
        else await LoadDatasetPairAsync(active);
    }

    private void DatasetPairPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _datasetPointerTarget = null;
        for (var visual = e.Source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is ListBoxItem { DataContext: DatasetListEntry entry })
            {
                _datasetPointerTarget = entry;
                break;
            }
            if (ReferenceEquals(visual, _datasetPairsList)) break;
        }
    }

    private async void DatasetPairSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_settingDatasetSelection || _datasetLoading) return;
        if (_datasetCaptionDirty)
        {
            _settingDatasetSelection = true;
            if (_datasetPairsList.SelectedItems is { } selection)
            {
                selection.Clear();
                if (_datasetPairsList.ItemsSource is IEnumerable<DatasetListEntry> entries)
                    foreach (var entry in entries.Where(entry => _datasetSelectedPaths.Contains(entry.Pair.MediaPath)))
                        selection.Add(entry);
            }
            _settingDatasetSelection = false;
            _datasetStatus.Text = "Save or discard the current caption edits before selecting another pair.";
            return;
        }
        try
        {
            var selected = _datasetPairsList.SelectedItems?.OfType<DatasetListEntry>().ToArray() ?? [];
            _datasetSelectedPaths = selected.Select(entry => entry.Pair.MediaPath)
                .ToHashSet(StringComparer.Ordinal);
            var active = selected.FirstOrDefault(entry => ReferenceEquals(entry, _datasetPointerTarget))
                         ?? e.AddedItems.OfType<DatasetListEntry>().LastOrDefault()
                         ?? selected.FirstOrDefault(entry => entry.Pair.MediaPath == _datasetSelected?.Pair.MediaPath)
                         ?? selected.LastOrDefault();
            _datasetPointerTarget = null;
            if (active is not null && !ReferenceEquals(active, _datasetSelected))
                await LoadDatasetPairAsync(active);
            else if (active is not null) UpdateDatasetButtons();
            else ClearDatasetSelection();
        }
        catch (Exception error) { _datasetStatus.Text = "Could not load pair: " + error.Message; }
    }

    private async Task LoadDatasetPairAsync(DatasetListEntry entry)
    {
        int version = ++_datasetSelectionVersion;
        _datasetPreviewCancellation?.Cancel();
        _datasetPreviewCancellation?.Dispose();
        var cancellation = _datasetPreviewCancellation = new CancellationTokenSource();
        _datasetPlayer?.Stop();
        _datasetVideoView.IsVisible = false;
        _datasetFirstFrameImage.Source = null;
        _datasetFirstFrameImage.IsVisible = false;
        _datasetFrame?.Dispose();
        _datasetFrame = null;
        _datasetPlayButton.Content = "Play";
        _datasetSelected = entry;
        _datasetSavedCaption = "";
        _settingDatasetCaption = true;
        _datasetCaption.Text = "";
        _settingDatasetCaption = false;
        _datasetCaptionDirty = false;
        _datasetCaption.IsEnabled = false;
        _datasetSelectedFile.Text = Path.GetFileName(entry.Pair.MediaPath);
        ToolTip.SetTip(_datasetSelectedFile, entry.Pair.MediaPath);
        _datasetAudioWarning.IsVisible = false;
        UpdateDatasetButtons();
        try
        {
            string caption = entry.Pair.CaptionPath is { } path && File.Exists(path)
                ? (await File.ReadAllTextAsync(path, cancellation.Token)).TrimEnd('\r', '\n') : "";
            if (!IsCurrentDatasetSelection(entry, version)) return;
            _datasetSavedCaption = caption;
            _settingDatasetCaption = true;
            _datasetCaption.Text = caption;
            _settingDatasetCaption = false;
            _datasetCaption.IsEnabled = !_isBusy;
            if (_libVlc is not null)
            {
                _datasetPlayer ??= new MediaPlayer(_libVlc);
                _datasetVideoView.MediaPlayer = _datasetPlayer;
                using var media = new LibVLCSharp.Shared.Media(_libVlc, entry.Pair.MediaPath, FromType.FromPath);
                if (OperatingSystem.IsLinux()) media.AddOption(":avcodec-hw=none");
                _datasetPlayer.Media = media;
            }
            UpdateDatasetButtons();
            var mediaInfo = await MediaProbe.ReadAsync(entry.Pair.MediaPath, cancellation.Token);
            if (!IsCurrentDatasetSelection(entry, version)) return;
            _datasetSelectedFile.Text += mediaInfo.HasVideo
                ? $"\n{mediaInfo.Width}×{mediaInfo.Height} · {mediaInfo.FrameRate:0.###} fps · " +
                  $"{Timecode.Format(mediaInfo.Duration)} · {mediaInfo.VideoCodec} video" +
                  (mediaInfo.HasAudio ? $" + {mediaInfo.AudioCodec} audio" : " · no audio")
                : $"\nAudio only · {Timecode.Format(mediaInfo.Duration)}";
            _datasetAudioWarning.IsVisible = mediaInfo.HasVideo &&
                (_activeProject?.MediaPolicy switch
                {
                    ProjectMediaPolicy.JointVideoAudio => !mediaInfo.HasAudio,
                    ProjectMediaPolicy.VideoOnly => mediaInfo.HasAudio,
                    _ => !mediaInfo.HasAudio
                });
            _datasetAudioWarning.Text = _activeProject?.MediaPolicy switch
            {
                ProjectMediaPolicy.JointVideoAudio =>
                    "No audio stream · this joint project cannot finalize this video.",
                ProjectMediaPolicy.VideoOnly =>
                    "Audio stream found · re-export without audio for this video-only project.",
                _ => "No audio stream · the stock joint LTX trainer skips this video."
            };
            if (mediaInfo.HasVideo)
            {
                byte[] bytes = await MediaExporter.FrameAsync(entry.Pair.MediaPath, 0, cancellation.Token);
                if (!IsCurrentDatasetSelection(entry, version)) return;
                await using var stream = new MemoryStream(bytes);
                _datasetFrame = new Bitmap(stream);
                _datasetFirstFrameImage.Source = _datasetFrame;
                _datasetFirstFrameImage.IsVisible = !_datasetVideoView.IsVisible;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (IsCurrentDatasetSelection(entry, version))
                _datasetSelectedFile.Text += "\nPreview unavailable: " + error.Message;
        }
    }

    private bool IsCurrentDatasetSelection(DatasetListEntry entry, int version) =>
        version == _datasetSelectionVersion && ReferenceEquals(_datasetSelected, entry);

    private void ClearDatasetSelection()
    {
        ++_datasetSelectionVersion;
        _datasetPreviewCancellation?.Cancel();
        _datasetPlayer?.Stop();
        _datasetVideoView.IsVisible = false;
        _datasetFirstFrameImage.Source = null;
        _datasetFirstFrameImage.IsVisible = false;
        _datasetFrame?.Dispose();
        _datasetFrame = null;
        _datasetPlayButton.Content = "Play";
        _datasetSelected = null;
        _datasetSelectedPaths.Clear();
        _datasetSavedCaption = "";
        _settingDatasetCaption = true;
        _datasetCaption.Text = "";
        _settingDatasetCaption = false;
        _datasetCaptionDirty = false;
        _datasetSelectedFile.Text = "Select a pair to review.";
        ToolTip.SetTip(_datasetSelectedFile, null);
        _datasetAudioWarning.IsVisible = false;
        _datasetCaption.IsEnabled = false;
        UpdateDatasetButtons();
    }

    private async void DatasetPlayClicked(object? sender, RoutedEventArgs e)
    {
        if (_closeCleanupStarted || _datasetStartingPlayback || _datasetPlayer is null || _datasetSelected is null) return;
        try
        {
            if (_datasetPlayer.IsPlaying)
            {
                _datasetPlayer.Pause();
                _datasetPlayButton.Content = "Play";
            }
            else
            {
                var selected = _datasetSelected;
                _datasetStartingPlayback = true;
                try
                {
                    if (_datasetPlayer.State == VLCState.Ended) _datasetPlayer.Stop();
                    if (selected.Pair.HasVideo)
                    {
                        _datasetVideoView.IsVisible = true;
                        _datasetFirstFrameImage.IsVisible = false;
                        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                        if (_closeCleanupStarted || !ReferenceEquals(_datasetSelected, selected)) return;
                    }
                    if (_datasetPlayer.Play()) _datasetPlayButton.Content = "Pause";
                    else
                    {
                        ShowDatasetFirstFrame();
                        _datasetStatus.Text = "Playback could not start for this pair.";
                    }
                }
                finally { _datasetStartingPlayback = false; }
            }
        }
        catch (Exception error)
        {
            if (!_closeCleanupStarted)
            {
                ShowDatasetFirstFrame();
                _datasetStatus.Text = "Playback failed: " + error.Message;
            }
        }
    }

    private void ShowDatasetFirstFrame()
    {
        _datasetVideoView.IsVisible = false;
        _datasetFirstFrameImage.IsVisible = _datasetFirstFrameImage.Source is not null;
        _datasetPlayButton.Content = "Play";
    }

    private async void DatasetSaveClicked(object? sender, RoutedEventArgs e)
    {
        if (_isBusy || _datasetSelected is null) return;
        string mediaPath = _datasetSelected.Pair.MediaPath;
        try
        {
            SetBusy(true, "Saving dataset caption…");
            string caption = _datasetCaption.Text ?? "";
            if (_datasetSelected.Pair.ReviewStatus == DatasetReviewStatus.Reviewed &&
                !string.Equals(caption, _datasetSavedCaption, StringComparison.Ordinal))
                await CatalogForPair(mediaPath).SetReviewStatusAsync(mediaPath, DatasetReviewStatus.Pending);
            await CaptionFiles.SaveAsync(mediaPath, caption);
            _datasetSavedCaption = caption;
            _datasetCaptionDirty = false;
            if (IsCurrentDatasetFolder()) await RefreshDatasetPairsAsync(selectPath: mediaPath);
            _datasetStatus.Text = "Caption saved. Mark it reviewed when it is ready for the final dataset.";
        }
        catch (Exception error) { _datasetStatus.Text = "Could not save caption: " + error.Message; }
        finally { SetBusy(false); UpdateDatasetButtons(); }
    }

    private void DatasetDiscardEditsClicked(object? sender, RoutedEventArgs e)
    {
        if (_isBusy || _datasetSelected is null) return;
        _settingDatasetCaption = true;
        _datasetCaption.Text = _datasetSavedCaption;
        _settingDatasetCaption = false;
        _datasetCaptionDirty = false;
        UpdateDatasetButtons();
        _datasetStatus.Text = "Unsaved dataset edits discarded.";
    }

    private async void DatasetReviewClicked(object? sender, RoutedEventArgs e)
    {
        await SetSelectedReviewStatusAsync(DatasetReviewStatus.Reviewed);
    }

    private async void DatasetPendingClicked(object? sender, RoutedEventArgs e)
    {
        await SetSelectedReviewStatusAsync(DatasetReviewStatus.Pending);
    }

    private async void DatasetExcludeClicked(object? sender, RoutedEventArgs e)
    {
        await SetSelectedReviewStatusAsync(DatasetReviewStatus.Excluded);
    }

    private DatasetListEntry[] SelectedDatasetEntries() =>
        _datasetPairsList.SelectedItems?.OfType<DatasetListEntry>().ToArray() ?? [];

    private async Task SetSelectedReviewStatusAsync(DatasetReviewStatus status)
    {
        if (_isBusy || _datasetCaptionDirty) return;
        var selected = SelectedDatasetEntries();
        if (selected.Length == 0) return;
        string? activePath = _datasetSelected?.Pair.MediaPath;
        int changed = 0;
        int skipped = 0;
        var errors = new List<string>();
        try
        {
            SetBusy(true, "Updating review status…");
            foreach (var entry in selected)
            {
                if (entry.Pair.ReviewStatus == status) continue;
                if (status == DatasetReviewStatus.Reviewed && entry.Pair.CaptionPath is null)
                {
                    skipped++;
                    continue;
                }
                try
                {
                    if (status == DatasetReviewStatus.Reviewed && _activeProject is { } project)
                    {
                        var media = await MediaProbe.ReadAsync(entry.Pair.MediaPath);
                        if (ProjectMediaPolicyRules.GetRejection(project.MediaPolicy,
                                media.HasVideo, media.HasAudio) is { } rejection)
                        {
                            errors.Add($"{Path.GetFileName(entry.Pair.MediaPath)}: {rejection}");
                            continue;
                        }
                        if (project.MediaPolicy == ProjectMediaPolicy.VideoOnly && media.HasAudio)
                        {
                            errors.Add($"{Path.GetFileName(entry.Pair.MediaPath)}: " +
                                "Re-export without audio for this video-only project.");
                            continue;
                        }
                    }
                    await CatalogForPair(entry.Pair.MediaPath).SetReviewStatusAsync(entry.Pair.MediaPath, status);
                    changed++;
                }
                catch (Exception error) { errors.Add($"{Path.GetFileName(entry.Pair.MediaPath)}: {error.Message}"); }
            }
            if (changed > 0 && IsCurrentDatasetFolder())
                await RefreshDatasetPairsAsync(selectPath: activePath);
            string verb = status switch
            {
                DatasetReviewStatus.Reviewed => "marked reviewed",
                DatasetReviewStatus.Excluded => "excluded",
                _ => "returned to Needs review"
            };
            _datasetStatus.Text = $"{changed} pair(s) {verb}.";
            if (skipped > 0)
                _datasetStatus.Text += $" {skipped} pair(s) need a saved caption.";
            if (errors.Count > 0)
                _datasetStatus.Text += " Could not update: " + string.Join(" ", errors);
        }
        catch (Exception error) { _datasetStatus.Text = "Could not change review status: " + error.Message; }
        finally { SetBusy(false); UpdateDatasetButtons(); }
    }

    private async void ChooseFinalDatasetParentClicked(object? sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose where to create the finalized dataset folder",
            AllowMultiple = false
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } parent)
            _finalDatasetPath.Text = Path.Combine(parent, _activeProject?.Name ?? "Select-a-project",
                "Dataset-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
    }

    private async void FinalizeDatasetClicked(object? sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        if (_activeProject is null || _activePreset is null)
        {
            _datasetStatus.Text = "Create or select a project before finalizing.";
            return;
        }
        if (_datasetCaptionDirty)
        {
            _datasetStatus.Text = "Save or discard the current caption edits before finalizing.";
            return;
        }
        try
        {
            string destination = Path.GetFullPath(_finalDatasetPath.Text?.Trim() ?? "");
            if (IsWithinDirectory(destination, _projectStore.ExportsRoot))
                throw new InvalidOperationException("Choose a finalized dataset location outside working exports.");
            SetBusy(true, "Finalizing reviewed pairs…");
            var catalog = CurrentCatalog();
            var selected = (await catalog.ListPairsAsync())
                .Where(pair => pair.HasVideo && pair.ReviewStatus == DatasetReviewStatus.Reviewed)
                .ToArray();
            ProjectMediaPolicy mediaPolicy = _activeProject.MediaPolicy;
            var result = await DatasetFinalizer.FinalizeAsync(selected, destination,
                mediaPolicy: mediaPolicy);
            var warnings = new List<string>();
            if (result.CleanupWarning is not null) warnings.Add(result.CleanupWarning);
            bool catalogCleanupSucceeded = true;
            foreach (var pair in selected)
                if (!File.Exists(pair.MediaPath) &&
                    (pair.CaptionPath is null || !File.Exists(pair.CaptionPath)))
                {
                    try { await catalog.RemoveExportAsync(pair.MediaPath); }
                    catch (Exception error)
                    {
                        catalogCleanupSucceeded = false;
                        warnings.Add($"Could not update the working catalog for {Path.GetFileName(pair.MediaPath)}: {error.Message}");
                    }
                }
            _datasetCaptionDirty = false;
            bool projectRetired = false;
            int remainingEntries = 0;
            if (result.RetainedSourcePaths.Count == 0 && result.CleanupWarning is null &&
                catalogCleanupSucceeded)
            {
                try
                {
                    var retirement = await _projectStore.TryRetireEmptyProjectAsync(_activeProject.Name);
                    projectRetired = retirement.Retired;
                    remainingEntries = retirement.RemainingEntries;
                    if (retirement.Warning is not null) warnings.Add(retirement.Warning);
                }
                catch (Exception error)
                {
                    warnings.Add("Could not remove the empty working project: " + error.Message);
                }
            }
            if (projectRetired)
                await LoadProjectsAsync(chooseSoleProject: false);
            else
            {
                try { await RefreshDatasetPairsAsync(); }
                catch (Exception error) { warnings.Add("Could not refresh the working list: " + error.Message); }
            }
            _datasetStatus.Text = result.RetainedSourcePaths.Count == 0
                ? $"Finalized {result.PairCount} pairs in {result.DirectoryPath}, with dataset.jsonl and training-recipe.sh. Copied working pairs were removed."
                : $"Finalized {result.PairCount} pairs in {result.DirectoryPath}. " +
                  $"Cleanup left {result.RetainedSourcePaths.Count} working file(s); check them before retrying.";
            if (projectRetired)
                _datasetStatus.Text += " The empty working project folder was removed; create or select a project for the next run.";
            else if (remainingEntries > 0)
            {
                int pending = _datasetAllPairs.Count(pair => pair.ReviewStatus == DatasetReviewStatus.Pending);
                int excluded = _datasetAllPairs.Count(pair => pair.ReviewStatus == DatasetReviewStatus.Excluded);
                _datasetStatus.Text += $" Working project retained: {pending} need review, {excluded} excluded, " +
                                       $"{remainingEntries} file(s) or folder(s) remain.";
            }
            if (result.VideosWithoutAudio > 0 && mediaPolicy == ProjectMediaPolicy.LegacyMixed)
                _datasetStatus.Text += $" {result.VideosWithoutAudio} video(s) have no audio and will be skipped by " +
                                       "the stock joint audio/video trainer; see training-recipe.sh.";
            if (warnings.Count > 0) _datasetStatus.Text += " " + string.Join(" ", warnings);
            _finalDatasetPath.Text = DefaultFinalDatasetPath();
        }
        catch (Exception error) { _datasetStatus.Text = "Finalization stopped: " + error.Message; }
        finally { SetBusy(false); UpdateDatasetButtons(); }
    }

    private void UpdateDatasetButtons()
    {
        if (_datasetSaveButton is null) return;
        bool selected = _datasetSelected is not null;
        var group = SelectedDatasetEntries();
        _datasetPlayButton.IsEnabled = selected && !_isBusy && !_datasetStartingPlayback && _datasetPlayer is not null;
        _datasetSaveButton.IsEnabled = selected && !_isBusy && _datasetCaptionDirty &&
                                       !string.IsNullOrWhiteSpace(_datasetCaption.Text);
        _datasetDiscardEdits.IsEnabled = selected && !_isBusy && _datasetCaptionDirty;
        _datasetReviewButton.IsEnabled = !_isBusy && !_datasetCaptionDirty &&
            group.Any(entry => entry.Pair.ReviewStatus != DatasetReviewStatus.Reviewed &&
                               entry.Pair.CaptionPath is not null);
        _datasetPendingButton.IsEnabled = !_isBusy && !_datasetCaptionDirty &&
            group.Any(entry => entry.Pair.ReviewStatus != DatasetReviewStatus.Pending);
        _datasetExcludeButton.IsEnabled = !_isBusy && !_datasetCaptionDirty &&
            group.Any(entry => entry.Pair.ReviewStatus != DatasetReviewStatus.Excluded);
        _datasetFilterCombo.IsEnabled = !_isBusy && !_datasetCaptionDirty && !_datasetLoading;
        _datasetPairsList.IsEnabled = !_isBusy && !_datasetLoading;
        _datasetSelectionText.Text = group.Length switch
        {
            0 => "Choose a pair to edit its caption.",
            1 => $"Editing {Path.GetFileName(_datasetSelected?.Pair.MediaPath)} · review actions apply to this pair.",
            _ => $"{group.Length} selected · editing {Path.GetFileName(_datasetSelected?.Pair.MediaPath)}. " +
                 "Save affects this caption; review actions affect all selected pairs."
        };
    }
}
