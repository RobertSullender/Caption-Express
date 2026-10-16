using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CaptionExpress.Caption;
using CaptionExpress.Dataset;
using CaptionExpress.Media;
using CaptionExpress.SpellCheck;
using LibVLCSharp.Avalonia;
using LibVLCSharp.Shared;

namespace CaptionExpress;

public partial class MainWindow : Window
{
    private readonly TextBox _sourcePath;
    private readonly Button _openButton;
    private readonly TextBlock _mediaInfo;
    private readonly TextBlock _durationText;
    private readonly TextBlock _playheadText;
    private readonly Slider _playheadSlider;
    private readonly TextBox _startBox;
    private readonly TextBox _endBox;
    private readonly Button _setStartButton;
    private readonly Button _setEndButton;
    private readonly TextBlock _ltxPlanText;
    private readonly Button _playButton;
    private readonly VideoView _videoView;
    private readonly Border _sourcePreviewBorder;
    private readonly Image _firstFrameImage;
    private readonly Image _waveformImage;
    private readonly ComboBox _bucketCombo;
    private readonly Button _nearestBucketButton;
    private readonly CropEditor _cropEditor;
    private readonly CropOutputPreview _cropOutput;
    private readonly Slider _cropXSlider;
    private readonly Slider _cropYSlider;
    private readonly Button _centerCropButton;
    private readonly TextBlock _cropXText;
    private readonly TextBlock _cropYText;
    private readonly StackPanel _cropOptions;
    private readonly TextBlock _audioOnlyText;
    private readonly TextBox _exportDirectory;
    private readonly StackPanel _videoFormatOptions;
    private readonly TextBlock _videoFormatText;
    private readonly TextBlock _exportedFileText;
    private readonly SpellCheckEditor _overallBox;
    private readonly Button _compareDraftButton;
    private readonly Button _saveButton;
    private readonly Button _discardEditsButton;
    private readonly Button _cancelButton;
    private readonly Button _exportButton;
    private readonly Button _captionButton;
    private readonly TextBox _pythonPath;
    private readonly ComboBox _modelCombo;
    private readonly Button _refreshModelsButton;
    private readonly Button _unloadModelButton;
    private readonly TextBlock _modelStatus;
    private readonly CheckBox _refineCaption;
    private readonly CheckBox _enableThinking;
    private readonly CheckBox _useDefaultSystem;
    private readonly SpellCheckEditor _systemPrompt;
    private readonly SpellCheckEditor _userPrompt;
    private readonly Button _resetUserPromptButton;
    private readonly SpellCheckEditor _guidance;
    private readonly TextBlock _guidanceReviewWarning;
    private readonly Button _confirmGuidanceButton;
    private readonly TextBlock _statusText;
    private readonly ProgressBar _progress;
    private readonly DispatcherTimer _playTimer;
    private readonly string _projectRoot;
    private readonly string _workerScript;

    private MediaInfo? _source;
    private ExportResult? _export;
    private string? _exportSignature;
    private Bitmap? _frame;
    private Bitmap? _waveform;
    private WorkerClient? _worker;
    private string? _workerPython;
    private readonly SemaphoreSlim _workerStartGate = new(1, 1);
    private LibVLC? _libVlc;
    private MediaPlayer? _player;
    private CancellationTokenSource? _jobCancellation;
    private CancellationTokenSource? _frameCancellation;
    private bool _settingPlayhead;
    private bool _settingCrop;
    private bool _settingModel;
    private bool _modelTransition;
    private bool _closingAfterCleanup;
    private bool _closeCleanupStarted;
    private bool _userPromptsInitialized;
    private bool _isBusy;
    private bool _openingMedia;
    private bool _startingPlayback;
    private bool _hasPlayedSource;
    private bool _settingCaptionText;
    private bool _captionDirty;
    private string _savedCaption = "";
    private string? _initialDraft;
    private string? _refinedDraft;
    private string? _displayedGeneratedDraft;
    private GuidanceContext? _guidanceContext;

    private sealed record ExportSelection(MediaInfo Source, string Directory, double Start, double End,
        Bucket? Bucket, double CropX, double CropY, VideoExportFormat VideoFormat,
        TrainingExportPreset Preset, double FrameRate, string Signature);
    private sealed record GuidanceContext(string SourcePath, double Start, double End,
        Bucket? Bucket, double CropX, double CropY, string ProjectDirectory, double FrameRate);

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _sourcePath = Get<TextBox>("SourcePathBox");
        _openButton = Get<Button>("OpenButton");
        _mediaInfo = Get<TextBlock>("MediaInfoText");
        _durationText = Get<TextBlock>("DurationText");
        _playheadText = Get<TextBlock>("PlayheadText");
        _playheadSlider = Get<Slider>("PlayheadSlider");
        _startBox = Get<TextBox>("StartBox");
        _endBox = Get<TextBox>("EndBox");
        _setStartButton = Get<Button>("SetStartButton");
        _setEndButton = Get<Button>("SetEndButton");
        _ltxPlanText = Get<TextBlock>("LtxPlanText");
        _playButton = Get<Button>("PlayButton");
        _videoView = Get<VideoView>("VideoView");
        _sourcePreviewBorder = Get<Border>("SourcePreviewBorder");
        SizeChanged += (_, e) =>
            _sourcePreviewBorder.Height = Math.Clamp(e.NewSize.Height - 630, 70, 240);
        _firstFrameImage = Get<Image>("FirstFrameImage");
        _waveformImage = Get<Image>("WaveformImage");
        _bucketCombo = Get<ComboBox>("BucketCombo");
        _nearestBucketButton = Get<Button>("NearestBucketButton");
        _cropEditor = Get<CropEditor>("CropEditor");
        _cropOutput = Get<CropOutputPreview>("CropOutputPreview");
        _cropXSlider = Get<Slider>("CropXSlider");
        _cropYSlider = Get<Slider>("CropYSlider");
        _centerCropButton = Get<Button>("CenterCropButton");
        _cropXText = Get<TextBlock>("CropXText");
        _cropYText = Get<TextBlock>("CropYText");
        _cropOptions = Get<StackPanel>("CropOptions");
        _audioOnlyText = Get<TextBlock>("AudioOnlyText");
        _exportDirectory = Get<TextBox>("ExportDirectoryBox");
        _videoFormatOptions = Get<StackPanel>("VideoFormatOptions");
        _videoFormatText = Get<TextBlock>("VideoFormatText");
        _exportedFileText = Get<TextBlock>("ExportedFileText");
        _overallBox = Get<SpellCheckEditor>("OverallBox");
        _compareDraftButton = Get<Button>("CompareDraftButton");
        _saveButton = Get<Button>("SaveButton");
        _discardEditsButton = Get<Button>("DiscardEditsButton");
        _cancelButton = Get<Button>("CancelButton");
        _exportButton = Get<Button>("ExportButton");
        _captionButton = Get<Button>("CaptionButton");
        _pythonPath = Get<TextBox>("PythonPathBox");
        _modelCombo = Get<ComboBox>("ModelCombo");
        _refreshModelsButton = Get<Button>("RefreshModelsButton");
        _unloadModelButton = Get<Button>("UnloadModelButton");
        _modelStatus = Get<TextBlock>("ModelStatusText");
        _refineCaption = Get<CheckBox>("RefineCaptionCheck");
        _enableThinking = Get<CheckBox>("EnableThinkingCheck");
        _useDefaultSystem = Get<CheckBox>("UseDefaultSystemCheck");
        _systemPrompt = Get<SpellCheckEditor>("SystemPromptBox");
        _userPrompt = Get<SpellCheckEditor>("UserPromptBox");
        _resetUserPromptButton = Get<Button>("ResetUserPromptButton");
        _guidance = Get<SpellCheckEditor>("GuidanceBox");
        _guidanceReviewWarning = Get<TextBlock>("GuidanceReviewWarning");
        _confirmGuidanceButton = Get<Button>("ConfirmGuidanceButton");
        _statusText = Get<TextBlock>("StatusText");
        _progress = Get<ProgressBar>("TaskProgress");

        _projectRoot = FindProjectRoot();
        _workerScript = Path.Combine(_projectRoot, "worker", "caption_worker.py");
        _exportDirectory.Text = "";
        InitializeProjectsUi();
        InitializeBatchUi();
        InitializeDatasetUi();
        _pythonPath.Text = FindPython(_projectRoot);
        InitializeModelDownloadUi();
        UpdateModelStatus();
        _bucketCombo.ItemsSource = Array.Empty<Bucket>();
        _cropOptions.IsVisible = false;

        _bucketCombo.SelectionChanged += (_, _) => { UpdateCropView(); UpdateLtxPlan(); };
        _cropXSlider.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) CropSliderChanged(); };
        _cropYSlider.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) CropSliderChanged(); };
        _cropEditor.CropChanged += (x, y) =>
        {
            _settingCrop = true;
            _cropXSlider.Value = x * 100;
            _cropYSlider.Value = y * 100;
            _settingCrop = false;
            UpdateCropView();
        };
        _playheadSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && !_settingPlayhead) PlayheadChanged();
        };
        _startBox.TextChanged += (_, _) => { UpdateLtxPlan(); UpdateGuidanceUi(); };
        _endBox.TextChanged += (_, _) => { UpdateLtxPlan(); UpdateGuidanceUi(); };
        _guidance.TextChanged += (_, _) =>
        {
            // Editing notes confirms the current source, trim, bucket, and crop.
            _guidanceContext = string.IsNullOrWhiteSpace(_guidance.Text) ? null : CurrentGuidanceContext();
            UpdateGuidanceUi();
        };
        _overallBox.TextChanged += (_, _) =>
        {
            if (!_settingCaptionText)
                _captionDirty = !string.Equals(_overallBox.Text ?? "", _savedCaption, StringComparison.Ordinal);
            _saveButton.IsEnabled = !_isBusy && _export is not null &&
                                    !string.IsNullOrWhiteSpace(_overallBox.Text);
            _discardEditsButton.IsEnabled = !_isBusy && _export is not null && _captionDirty;
            UpdateCompareDraftButton();
        };
        _useDefaultSystem.IsCheckedChanged += (_, _) =>
            _systemPrompt.IsEnabled = !_isBusy && _useDefaultSystem.IsChecked != true;
        _modelCombo.SelectionChanged += async (_, _) => await ModelConfigurationChangedAsync();
        _enableThinking.IsCheckedChanged += async (_, _) => await ModelConfigurationChangedAsync();

        _playTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _playTimer.Tick += (_, _) =>
        {
            if (_datasetPlayer is { IsPlaying: false, State: VLCState.Ended })
                ShowDatasetFirstFrame();
            if (_player is null || _source is null) return;
            if (!_player.IsPlaying)
            {
                if (_player.State is VLCState.Ended or VLCState.Paused or VLCState.Stopped)
                    _playButton.Content = "Play";
                return;
            }
            _settingPlayhead = true;
            _playheadSlider.Value = Math.Clamp(_player.Time / 1000.0, 0, _source.Duration);
            _playheadText.Text = Timecode.Format(_playheadSlider.Value);
            _settingPlayhead = false;
        };
        _playTimer.Start();

        try
        {
            Core.Initialize();
            _libVlc = OperatingSystem.IsLinux()
                ? new LibVLC("--avcodec-hw=none", "--vout=xcb_x11")
                : new LibVLC();
            _player = new MediaPlayer(_libVlc);
            _videoView.MediaPlayer = _player;
        }
        catch (Exception error) when (error is DllNotFoundException or
                                      EntryPointNotFoundException or
                                      BadImageFormatException or
                                      TypeInitializationException or
                                      LibVLCSharp.Shared.VLCException)
        {
            _playButton.IsEnabled = false;
            string detail = error.GetBaseException().Message;
            SetStatus("Playback unavailable: " + detail + " Launch with scripts/run.sh to set up local LibVLC.");
            Console.Error.WriteLine("LibVLC playback setup failed: " + error);
        }
        Closing += (_, e) =>
        {
            if (_closingAfterCleanup) return;
            _player?.Stop();
            _videoView.MediaPlayer = null;
            e.Cancel = true;
            if (_closeCleanupStarted) return;
            _closeCleanupStarted = true;
            _ = CloseAfterCleanupAsync();
        };
        Dispatcher.UIThread.Post(() => _ = RefreshModelsAsync());
        Dispatcher.UIThread.Post(async () => await LoadProjectsAsync());
    }

    private T Get<T>(string name) where T : Control => this.FindControl<T>(name)!;

    private void UpdateModelStatus()
    {
        string availability;
        if (_worker is null)
            availability = "Scanning local Gemma 4 GGUF models…";
        else if (_worker.Models.Count == 0)
            availability = "No Gemma 4 E2B/E4B GGUF and matching mmproj pair is ready. Choose models below to download into the app; safetensors cannot be loaded by this backend.";
        else if (_modelCombo.SelectedItem is WorkerModel model)
            availability = $"Selected: {model.Family} with its matching projector. {Path.GetFileName(model.Id)}";
        else
            availability = "Select a Gemma 4 model.";
        string loaded = _worker?.LoadedModelId is { } id
            ? $"Loaded: {_worker.LoadedModelLabel ?? Path.GetFileName(id)} (thinking {(_worker.LoadedModelThinking == true ? "on" : "off")})."
            : "No model loaded. The selected model will load on the next caption run.";
        _modelStatus.Text = availability + "\n" + loaded;
        _unloadModelButton.IsEnabled = !_isBusy && !_modelTransition && _worker?.LoadedModelId is not null;
    }

    private void ApplyModels()
    {
        if (!_userPromptsInitialized && _worker is not null)
        {
            // The model scan runs in the background; preserve a prompt entered before it finishes.
            if (string.IsNullOrWhiteSpace(_userPrompt.Text)) RestoreDefaultUserPrompt();
            _userPromptsInitialized = true;
        }
        string? previous = (_modelCombo.SelectedItem as WorkerModel)?.Id;
        var models = _worker?.Models ?? [];
        string projectModels = Path.GetFullPath(Path.Combine(_projectRoot, "models")) + Path.DirectorySeparatorChar;
        WorkerModel? appCopy = previous is not null && _worker?.LoadedModelId is null
            ? models.FirstOrDefault(model => model.Id.StartsWith(projectModels, StringComparison.Ordinal) &&
                string.Equals(Path.GetFileName(model.Id), Path.GetFileName(previous), StringComparison.Ordinal))
            : null;
        _settingModel = true;
        try
        {
            _modelCombo.ItemsSource = models;
            _modelCombo.SelectedItem = appCopy
                ?? models.FirstOrDefault(model => model.Id == previous)
                ?? models.FirstOrDefault(model => model.Id == _worker?.DefaultModelId)
                ?? models.FirstOrDefault();
        }
        finally { _settingModel = false; }
        _modelCombo.IsEnabled = models.Count > 0 && !_isBusy && !_modelTransition;
        UpdateModelStatus();
        RefreshModelInstallStatus();
    }

    private async Task ModelConfigurationChangedAsync()
    {
        if (_settingModel || _isBusy || _modelTransition)
        {
            UpdateModelStatus();
            return;
        }
        if (_worker?.LoadedModelId is null)
        {
            UpdateModelStatus();
            return;
        }
        string? selectedId = (_modelCombo.SelectedItem as WorkerModel)?.Id;
        if (selectedId == _worker.LoadedModelId &&
            (_enableThinking.IsChecked == true) == _worker.LoadedModelThinking)
        {
            UpdateModelStatus();
            return;
        }
        await UnloadModelAsync();
    }

    private async void UnloadModelClicked(object? sender, RoutedEventArgs e) => await UnloadModelAsync();

    private async Task UnloadModelAsync()
    {
        if (_isBusy || _modelTransition || _worker?.LoadedModelId is null) return;
        _modelTransition = true;
        _modelStatus.Text = "Unloading Gemma model…";
        SetBusy(true, "Unloading Gemma model…");
        try
        {
            await _worker.RequestAsync(new { command = "unload_model" });
            SetStatus("Gemma model unloaded. The selected model will load on the next caption run.");
        }
        catch (Exception error)
        {
            await ResetWorkerAsync();
            ShowError("Could not unload model: " + error.Message);
        }
        finally
        {
            _modelTransition = false;
            SetBusy(false);
            UpdateModelStatus();
        }
    }

    private async void RefreshModelsClicked(object? sender, RoutedEventArgs e) => await RefreshModelsAsync();

    private void ResetUserPromptClicked(object? sender, RoutedEventArgs e)
    {
        if (_worker is null)
        {
            SetStatus("The default user prompt is available after the model scan.");
            return;
        }
        RestoreDefaultUserPrompt();
        SetStatus("Default caption user prompt restored.");
    }

    private void RestoreDefaultUserPrompt()
    {
        _userPrompt.Text = _worker?.DefaultUserPrompt;
    }

    private async Task RefreshModelsAsync()
    {
        _refreshModelsButton.IsEnabled = false;
        try
        {
            var worker = await EnsureWorkerAsync(CancellationToken.None);
            await worker.RequestAsync(new { command = "status" });
            ApplyModels();
            await ModelConfigurationChangedAsync();
        }
        catch (Exception error)
        {
            await ResetWorkerAsync();
            _modelStatus.Text = "Model scan failed: " + error.Message;
        }
        finally { _refreshModelsButton.IsEnabled = !_isBusy && !_modelTransition; }
    }

    private void UpdateLtxPlan()
    {
        if (_source?.HasVideo != true) return;
        if (_activePreset is null)
        {
            _ltxPlanText.Text = "Choose a project to preview output duration and frame count.";
            return;
        }
        if (!Timecode.TryParse(_startBox.Text, out double start) ||
            !Timecode.TryParse(_endBox.Text, out double end))
        {
            _ltxPlanText.Text = "Enter a valid range to preview the export frame count.";
            return;
        }
        try
        {
            var plan = TrainingExportPlan.ForRange(start, end, _source.Duration,
                _activePreset, ResolveFrameRate());
            var bucket = _bucketCombo.SelectedItem as Bucket;
            string exportSize = bucket is null ? "" :
                $" · export size {bucket.Width}×{bucket.Height}×{plan.FrameCount}";
            _ltxPlanText.Text = $"{_activePreset.Name}: {plan.FrameRate:0.###} fps · {plan.FrameCount} frames " +
                $"({_activePreset.TemporalFactor}n+1) · {Timecode.Format(plan.Duration)} output duration{exportSize} · " +
                $"last sampled source frame {Timecode.Format(plan.SourceEnd)}. " +
                "The final frame's display time can make the file slightly longer than the selected range. " +
                "Set the trainer's frame bucket to match; " +
                "a smaller bucket may train only the first frames.";
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { _ltxPlanText.Text = error.Message; }
    }

    private async void OpenClicked(object? sender, RoutedEventArgs e)
    {
        if (_isBusy || !CanReplaceCaption()) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose one video or audio file", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Video and audio")
            {
                Patterns = ["*.mp4", "*.mov", "*.mkv", "*.webm", "*.avi", "*.mp3", "*.wav", "*.flac", "*.m4a", "*.ogg"]
            }, FilePickerFileTypes.All]
        });
        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (path is null) { ShowError("Choose a local file."); return; }
        if (_isBusy || !CanReplaceCaption()) return;
        try
        {
            SetBusy(true, "Reading media…");
            _openingMedia = true;
            _player?.Stop();
            _playButton.IsEnabled = false;
            _playheadSlider.IsEnabled = false;
            _frameCancellation?.Cancel();
            _firstFrameImage.Source = null;
            _firstFrameImage.IsVisible = false;
            _videoView.IsVisible = false;
            _hasPlayedSource = false;
            _frame?.Dispose();
            _frame = null;
            UpdateCropView();
            var info = await MediaProbe.ReadAsync(path);
            _source = info;
            UpdateModelStatus();
            _sourcePath.Text = path;
            _mediaInfo.Text = $"{info.DisplaySize}  ·  {Timecode.Format(info.Duration)}";
            _durationText.Text = Timecode.Format(info.Duration);
            _startBox.Text = Timecode.Format(0);
            _endBox.Text = Timecode.Format(info.Duration);
            _settingPlayhead = true;
            _playheadSlider.Maximum = info.Duration;
            _playheadSlider.Value = 0;
            _settingPlayhead = false;
            _playheadText.Text = Timecode.Format(0);
            _export = null;
            _exportSignature = null;
            _exportedFileText.Text = "No cropped clip exported yet.";
            _cropOptions.IsVisible = info.HasVideo;
            _videoFormatOptions.IsVisible = info.HasVideo;
            _frameRateOptions.IsVisible = info.HasVideo;
            _ltxPlanText.IsVisible = info.HasVideo;
            _audioOnlyText.IsVisible = !info.HasVideo;
            _waveformImage.IsVisible = !info.HasVideo;
            _bucketCombo.SelectedItem = info.HasVideo && _activePreset is not null
                ? _activePreset.NearestBucket(info.Width, info.Height) : null;
            RebuildFrameRateChoices((_frameRateCombo.SelectedItem as FrameRateChoice)?.Value);
            _cropXSlider.Value = 50;
            _cropYSlider.Value = 50;
            UpdateLtxPlan();
            ClearCaptions();
            UpdateGuidanceUi();
            if (_player is not null && _libVlc is not null)
            {
                _player.Stop();
                using var media = new LibVLCSharp.Shared.Media(_libVlc, path, FromType.FromPath);
                if (OperatingSystem.IsLinux()) media.AddOption(":avcodec-hw=none");
                _player.Media = media;
            }
            _playButton.Content = "Play";
            await RefreshFrameAsync();
            string? projectRejection = _activeProject is { } activeProject
                ? ProjectMediaPolicyRules.GetRejection(activeProject.MediaPolicy, info.HasVideo, info.HasAudio)
                : null;
            SetStatus(projectRejection is null
                ? "Ready. Choose the range and crop position."
                : $"This source is outside the project's media type: {projectRejection}");
        }
        catch (Exception error) { ShowError(error.Message); }
        finally
        {
            _openingMedia = false;
            _playButton.IsEnabled = _player is not null && _source is not null;
            _playheadSlider.IsEnabled = _source is not null;
            SetBusy(false);
        }
    }

    private async void PlayClicked(object? sender, RoutedEventArgs e)
    {
        if (_closeCleanupStarted || _openingMedia || _startingPlayback || _player is null || _source is null) return;
        try
        {
            if (_player.IsPlaying)
            {
                _player.Pause();
                _playButton.Content = "Play";
                return;
            }
            var source = _source;
            _startingPlayback = true;
            bool restarting = _player.State == VLCState.Ended;
            if (restarting) _player.Stop();
            if (source.HasVideo)
            {
                _videoView.IsVisible = true;
                _firstFrameImage.IsVisible = false;
                // The native VLC window is created when the view becomes visible.
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                if (_closeCleanupStarted || _openingMedia || !ReferenceEquals(_source, source)) return;
            }
            if (_player.Play())
            {
                _hasPlayedSource = true;
                _playButton.Content = "Pause";
                if (restarting)
                {
                    _settingPlayhead = true;
                    _playheadSlider.Value = 0;
                    _playheadText.Text = Timecode.Format(0);
                    _settingPlayhead = false;
                }
            }
            else
            {
                _videoView.IsVisible = false;
                _firstFrameImage.IsVisible = source.HasVideo && _firstFrameImage.Source is not null;
                SetStatus("Playback could not start for this file.");
            }
        }
        catch (Exception error)
        {
            if (!_closeCleanupStarted)
            {
                _videoView.IsVisible = false;
                _firstFrameImage.IsVisible = _source?.HasVideo == true && _firstFrameImage.Source is not null;
                SetStatus("Playback failed: " + error.Message);
            }
        }
        finally { _startingPlayback = false; }
    }

    private void SetStartClicked(object? sender, RoutedEventArgs e) => _startBox.Text = Timecode.Format(_playheadSlider.Value);
    private void SetEndClicked(object? sender, RoutedEventArgs e) => _endBox.Text = Timecode.Format(_playheadSlider.Value);

    private void PlayheadChanged()
    {
        _playheadText.Text = Timecode.Format(_playheadSlider.Value);
        if (_player is not null && _source is not null) _player.Time = (long)(_playheadSlider.Value * 1000);
        _ = RefreshFrameAsync();
    }

    private async Task RefreshFrameAsync()
    {
        if (_source is null) return;
        _frameCancellation?.Cancel();
        _frameCancellation?.Dispose();
        var cancellation = _frameCancellation = new CancellationTokenSource();
        try
        {
            await Task.Delay(180, cancellation.Token);
            double second = Math.Min(_playheadSlider.Value, Math.Max(0, _source.Duration - 0.05));
            var bytes = _source.HasVideo
                ? await MediaExporter.FrameAsync(_source.Path, second, cancellation.Token)
                : await MediaExporter.WaveformAsync(_source.Path, second, _source.Duration, cancellation.Token);
            await using var stream = new MemoryStream(bytes);
            var frame = new Bitmap(stream);
            if (cancellation.IsCancellationRequested) { frame.Dispose(); return; }
            if (_source.HasVideo)
            {
                _firstFrameImage.Source = null;
                _frame?.Dispose();
                _frame = frame;
                if (!_hasPlayedSource && !_videoView.IsVisible)
                {
                    _firstFrameImage.Source = frame;
                    _firstFrameImage.IsVisible = true;
                }
                UpdateCropView();
            }
            else
            {
                _waveform?.Dispose();
                _waveform = frame;
                _waveformImage.Source = frame;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { SetStatus("Preview: " + error.Message); }
    }

    private void CropSliderChanged()
    {
        if (_settingCrop) return;
        UpdateCropView();
    }

    private void UpdateCropView()
    {
        var bucket = _bucketCombo.SelectedItem as Bucket;
        double x = _cropXSlider.Value / 100;
        double y = _cropYSlider.Value / 100;
        _cropXText.Text = $"{_cropXSlider.Value:0}%";
        _cropYText.Text = $"{_cropYSlider.Value:0}%";
        _cropEditor.SetFrame(_frame);
        _cropEditor.SetBucket(bucket);
        _cropEditor.SetPosition(x, y);
        _cropOutput.Update(_frame, bucket, x, y);
        UpdateGuidanceUi();
    }

    private void CenterCropClicked(object? sender, RoutedEventArgs e)
    {
        _cropXSlider.Value = 50;
        _cropYSlider.Value = 50;
    }

    private void NearestBucketClicked(object? sender, RoutedEventArgs e)
    {
        if (_source?.HasVideo == true && _activePreset is not null)
            _bucketCombo.SelectedItem = _activePreset.NearestBucket(_source.Width, _source.Height);
    }

    private async void ExportClicked(object? sender, RoutedEventArgs e)
    {
        if (_isBusy || !CanReplaceCaption()) return;
        try
        {
            StartJob("Exporting new clip…");
            var selection = CurrentSelection();
            await ExportCurrentAsync(selection, _jobCancellation!.Token);
            SetStatus("Export complete. The original file is untouched.");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Export canceled.");
            await ResetWorkerAsync();
        }
        catch (Exception error) { ShowError(error.Message); }
        finally { FinishJob(); }
    }

    private async Task<ExportResult> ExportCurrentAsync(ExportSelection selection, CancellationToken cancellationToken)
    {
        bool includeAudio = _activeProject?.MediaPolicy != ProjectMediaPolicy.VideoOnly;
        var export = await MediaExporter.ExportAsync(selection.Source, selection.Directory,
            selection.Start, selection.End, selection.Bucket, selection.CropX, selection.CropY, cancellationToken,
            selection.VideoFormat, selection.Preset, selection.FrameRate, includeAudio);
        _export = export;
        _exportSignature = selection.Signature;
        _exportedFileText.Text = export.FrameCount is int frames
            ? $"{export.Path}\n{export.FrameRate:0.###} fps · {frames} frames · " +
              $"{Timecode.Format(export.Duration)} output duration · last sampled source frame " +
              $"{Timecode.Format(export.LastSourceFrameTime ?? selection.Start + export.Duration)}"
            : export.Path;
        ClearCaptions();
        try
        {
            var catalog = new DatasetCatalog(selection.Directory);
            string hash = await catalog.ComputeSourceHashAsync(selection.Source.Path, cancellationToken);
            await catalog.RecordExportAsync(export.Path, hash, cancellationToken,
                videoFormatId: selection.VideoFormat.Id);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _exportedFileText.Text += "\nDuplicate tracking unavailable: " + error.Message;
        }
        return export;
    }

    private ExportSelection CurrentSelection()
    {
        var source = _source ?? throw new InvalidOperationException("Open a source file first.");
        if (!Timecode.TryParse(_startBox.Text, out double start) ||
            !Timecode.TryParse(_endBox.Text, out double end) ||
            end <= start || end > source.Duration + 0.05)
            throw new InvalidOperationException("Enter a valid start and end within the source duration.");
        var project = _activeProject ?? throw new InvalidOperationException("Create or select a project first.");
        if (ProjectMediaPolicyRules.GetRejection(project.MediaPolicy, source.HasVideo, source.HasAudio) is { } rejection)
            throw new InvalidOperationException(rejection);
        var preset = _activePreset ?? throw new InvalidOperationException("This project has no export preset.");
        var directory = project.DirectoryPath;
        var bucket = source.HasVideo ? _bucketCombo.SelectedItem as Bucket : null;
        if (source.HasVideo && bucket is null) throw new InvalidOperationException("Choose a video bucket.");
        if (bucket is not null) preset.ValidateBucket(bucket);
        double x = _cropXSlider.Value / 100;
        double y = _cropYSlider.Value / 100;
        var videoFormat = preset.DefaultVideoFormat;
        double frameRate = source.HasVideo ? ResolveFrameRate() : preset.DefaultFrameRate;
        string signature = string.Join("|", source.Path, directory, start.ToString("R"), end.ToString("R"),
            preset.Id, bucket?.Width, bucket?.Height, x.ToString("R"), y.ToString("R"),
            videoFormat.Id, frameRate.ToString("R"));
        return new ExportSelection(source, directory, start, end, bucket, x, y, videoFormat,
            preset, frameRate, signature);
    }

    private GuidanceContext? CurrentGuidanceContext()
    {
        if (_source is null ||
            _activeProject is null || _activePreset is null ||
            !Timecode.TryParse(_startBox.Text, out double start) ||
            !Timecode.TryParse(_endBox.Text, out double end) ||
            end <= start || end > _source.Duration + 0.05)
            return null;
        var bucket = _source.HasVideo ? _bucketCombo.SelectedItem as Bucket : null;
        if (_source.HasVideo && bucket is null) return null;
        return new GuidanceContext(_source.Path, start, end, bucket,
            _source.HasVideo ? _cropXSlider.Value / 100 : 0,
            _source.HasVideo ? _cropYSlider.Value / 100 : 0,
            _activeProject.DirectoryPath,
            _source.HasVideo ? ResolveFrameRate() : _activePreset.DefaultFrameRate);
    }

    private static GuidanceContext GuidanceFor(ExportSelection selection) =>
        new(selection.Source.Path, selection.Start, selection.End, selection.Bucket,
            selection.Source.HasVideo ? selection.CropX : 0,
            selection.Source.HasVideo ? selection.CropY : 0,
            selection.Directory, selection.FrameRate);

    private void UpdateGuidanceUi()
    {
        bool needsReview = !string.IsNullOrWhiteSpace(_guidance.Text) &&
                           (_guidanceContext is null || _guidanceContext != CurrentGuidanceContext());
        _guidanceReviewWarning.IsVisible = needsReview;
        _confirmGuidanceButton.IsVisible = needsReview;
        _confirmGuidanceButton.IsEnabled = needsReview && !_isBusy && CurrentGuidanceContext() is not null;
    }

    private void ConfirmGuidanceClicked(object? sender, RoutedEventArgs e)
    {
        if (_isBusy || string.IsNullOrWhiteSpace(_guidance.Text)) return;
        _guidanceContext = CurrentGuidanceContext();
        UpdateGuidanceUi();
        if (_guidanceContext is not null)
            SetStatus("Guidance confirmed for the selected source, trim, bucket, and crop.");
    }

    private async void CaptionClicked(object? sender, RoutedEventArgs e)
    {
        if (_isBusy || _modelTransition || !CanReplaceCaption()) return;
        string? requestedModelId = (_modelCombo.SelectedItem as WorkerModel)?.Id;
        bool workerRequestPending = false;
        try
        {
            StartJob("Preparing caption job…");
            var selection = CurrentSelection();
            string? guidanceText = string.IsNullOrWhiteSpace(_guidance.Text) ? null : _guidance.Text.Trim();
            if (guidanceText is not null && _guidanceContext != GuidanceFor(selection))
                throw new InvalidOperationException("Review guidance for the selected source, trim, bucket, and crop; " +
                    "then edit the notes or select " +
                    "Use guidance for current clip before captioning. Clear the notes to run without guidance.");
            var worker = await EnsureWorkerAsync(_jobCancellation!.Token);
            workerRequestPending = true;
            await worker.RequestAsync(new { command = "status" },
                cancellationToken: _jobCancellation.Token);
            workerRequestPending = false;
            ApplyModels();
            if (requestedModelId is not null &&
                (_modelCombo.SelectedItem as WorkerModel)?.Id != requestedModelId)
            {
                ShowError("The selected Gemma model is no longer available. Choose an available model and retry.");
                return;
            }
            var model = _modelCombo.SelectedItem as WorkerModel
                ?? throw new InvalidOperationException("Install a Gemma 4 E2B/E4B GGUF with its matching mmproj, then refresh models.");
            if (_useDefaultSystem.IsChecked != true &&
                string.IsNullOrWhiteSpace(_systemPrompt.Text))
                throw new InvalidOperationException("Enter a custom system prompt or select the model default.");
            string userPrompt = _userPrompt.Text ?? "";
            if (selection.Source.HasVideo && string.IsNullOrWhiteSpace(PromptForMode(userPrompt, "video")))
                throw new InvalidOperationException("Enter instructions for the video caption.");
            bool captionHasAudio = selection.Source.HasAudio &&
                                   _activeProject?.MediaPolicy != ProjectMediaPolicy.VideoOnly;
            if (captionHasAudio && string.IsNullOrWhiteSpace(PromptForMode(userPrompt,
                    selection.Source.HasVideo ? "soundtrack" : "audio_only")))
                throw new InvalidOperationException("Enter instructions for the soundtrack caption.");
            if (_refineCaption.IsChecked == true && selection.Source.HasVideo &&
                captionHasAudio && string.IsNullOrWhiteSpace(PromptForMode(userPrompt, "refine")))
                throw new InvalidOperationException("Enter refinement instructions or turn off Refine combined caption.");
            if (guidanceText is not null)
            {
                double exportedDuration = selection.Source.HasVideo
                    ? TrainingExportPlan.ForRange(selection.Start, selection.End, selection.Source.Duration,
                        selection.Preset, selection.FrameRate).Duration
                    : selection.End - selection.Start;
                await worker.RequestAsync(new
                {
                    command = "validate_guidance",
                    guidance_text = guidanceText,
                    duration = exportedDuration
                }, cancellationToken: _jobCancellation.Token);
            }
            if (_export is null || _exportSignature != selection.Signature || !File.Exists(_export.Path))
            {
                SetStatus("Exporting the selected crop before captioning…");
                await ExportCurrentAsync(selection, _jobCancellation!.Token);
            }
            var export = _export!;
            bool rerun = !string.IsNullOrWhiteSpace(_savedCaption);
            var request = new
            {
                command = "caption",
                path = export.Path,
                model_id = model.Id,
                enable_thinking = _enableThinking.IsChecked == true,
                refine_caption = _refineCaption.IsChecked == true &&
                                 selection.Source.HasVideo && captionHasAudio,
                system_prompt = _useDefaultSystem.IsChecked == true ? null : _systemPrompt.Text,
                user_prompt = _userPrompt.Text,
                guidance_text = guidanceText,
                variation_seed = rerun ? RandomNumberGenerator.GetInt32(1, int.MaxValue) : (int?)null
            };
            workerRequestPending = true;
            var result = await worker.RequestAsync(request, HandleWorkerEventAsync, _jobCancellation.Token);
            workerRequestPending = false;
            UpdateModelStatus();
            string? refinementError = result.TryGetProperty("refinement_error", out var refinementFailure) &&
                refinementFailure.ValueKind == JsonValueKind.String ? refinementFailure.GetString() : null;
            string? refinementSkipped = result.TryGetProperty("refinement_skipped_reason", out var skipped) &&
                skipped.ValueKind == JsonValueKind.String ? skipped.GetString() : null;
            string? audioWarning = result.TryGetProperty("audio_warning", out var audioIssue) &&
                audioIssue.ValueKind == JsonValueKind.String ? audioIssue.GetString() : null;
            string caption = result.GetProperty("caption").GetString() ?? "";
            SetCaptionText(caption);
            bool refined = result.TryGetProperty("refinement_applied", out var applied) &&
                           applied.ValueKind == JsonValueKind.True;
            string? initial = result.TryGetProperty("initial_caption", out var initialValue) &&
                              initialValue.ValueKind == JsonValueKind.String ? initialValue.GetString() : null;
            SetDraftComparison(refined ? initial : null, refined ? caption : null);
            if (rerun)
                SetStatus(refinementError is not null
                    ? "Refinement failed; initial alternative draft is shown. Review it before saving."
                    : refinementSkipped is not null
                    ? "Refinement skipped because soundtrack observations were unavailable; the initial alternative draft is shown."
                    : _captionDirty
                    ? "Alternative draft ready. Save caption to replace the saved text, or discard to restore it."
                    : "This draft matches the saved caption. Try again or adjust the prompt.");
            else
            {
                await SaveCaptionsAsync(export.Path, _jobCancellation.Token);
                SetStatus(refinementError is not null
                    ? "Refinement failed; initial draft saved beside the export. Review it before training."
                    : refinementSkipped is not null
                    ? "Refinement skipped because soundtrack observations were unavailable; the initial draft was saved."
                    : "Caption saved beside the export. Edit the text, then select Save caption.");
            }
            if (audioWarning is not null)
                SetStatus((_statusText.Text ?? "") + " " + audioWarning);
        }
        catch (OperationCanceledException) { SetStatus("Captioning canceled."); await ResetWorkerAsync(); }
        catch (Exception error)
        {
            ShowError(error.Message);
            if (workerRequestPending || _worker is { IsAlive: false }) await ResetWorkerAsync();
        }
        finally
        {
            FinishJob();
            await ModelConfigurationChangedAsync();
            UpdateModelStatus();
        }
    }

    private static string PromptForMode(string prompt, string mode)
    {
        string first = Regex.Replace(prompt, @"\[\[later(?:_audio)?:\s*(.*?)\]\]", "", RegexOptions.Singleline);
        return Regex.Replace(first, @"\[\[(video|soundtrack|audio_only|refine):\s*(.*?)\]\]",
            match => match.Groups[1].Value == mode ? match.Groups[2].Value : "",
            RegexOptions.Singleline);
    }

    private async Task HandleWorkerEventAsync(JsonElement message)
    {
        void Apply()
        {
            string type = message.GetProperty("type").GetString() ?? "";
            if (type == "progress")
                SetStatus(message.TryGetProperty("message", out var text) ? text.GetString() ?? "Working…" : "Working…");
            else if (type == "section")
            {
                int index = message.GetProperty("index").GetInt32() + 1;
                int total = message.GetProperty("total").GetInt32();
                _progress.IsIndeterminate = false;
                _progress.Value = 100.0 * index / total;
                SetStatus($"Captioned section {index} of {total}.");
            }
        }
        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else await Dispatcher.UIThread.InvokeAsync(Apply);
    }

    private async Task<WorkerClient> EnsureWorkerAsync(CancellationToken cancellationToken)
    {
        await _workerStartGate.WaitAsync(cancellationToken);
        try
        {
            string python = _pythonPath.Text?.Trim() ?? "";
            if (python.Length == 0) throw new InvalidOperationException("Set the Python executable in Model and prompts.");
            if (_worker is { IsAlive: true } && _workerPython == python) return _worker;
            await ResetWorkerAsync();
            if (!File.Exists(_workerScript)) throw new FileNotFoundException("Caption worker script missing.", _workerScript);
            _worker = await WorkerClient.StartAsync(python, _workerScript, cancellationToken);
            _workerPython = python;
            ApplyModels();
            return _worker;
        }
        finally { _workerStartGate.Release(); }
    }

    private void CancelClicked(object? sender, RoutedEventArgs e) => _jobCancellation?.Cancel();

    private async void SaveClicked(object? sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        try
        {
            SetBusy(true, "Saving caption…");
            await SaveCaptionsAsync();
            SetStatus("Caption saved beside the exported clip.");
        }
        catch (Exception error) { ShowError(error.Message); }
        finally { SetBusy(false); }
    }

    private void CompareDraftClicked(object? sender, RoutedEventArgs e)
    {
        if (_isBusy || _initialDraft is null || _refinedDraft is null ||
            !string.Equals(_overallBox.Text ?? "", _displayedGeneratedDraft, StringComparison.Ordinal))
            return;
        bool showInitial = string.Equals(_displayedGeneratedDraft, _refinedDraft, StringComparison.Ordinal);
        _displayedGeneratedDraft = showInitial ? _initialDraft : _refinedDraft;
        SetCaptionText(_displayedGeneratedDraft);
        UpdateCompareDraftButton();
        SetStatus(showInitial
            ? "Showing the initial draft from this run. Save caption to use it for the text pair."
            : "Showing the refined draft from this run. Save caption to use it for the text pair.");
    }

    private void DiscardEditsClicked(object? sender, RoutedEventArgs e)
    {
        if (_isBusy || !_captionDirty) return;
        if (_savedCaption == _initialDraft) _displayedGeneratedDraft = _initialDraft;
        else if (_savedCaption == _refinedDraft) _displayedGeneratedDraft = _refinedDraft;
        else ClearDraftComparison();
        SetCaptionText(_savedCaption);
        _discardEditsButton.IsEnabled = false;
        SetStatus("Unsaved edits discarded. The saved caption is restored.");
    }

    private async Task SaveCaptionsAsync(string? exportedMediaPath = null,
        CancellationToken cancellationToken = default)
    {
        string path = exportedMediaPath ?? _export?.Path ??
            throw new InvalidOperationException("No exported clip to save with captions.");
        string caption = _overallBox.Text ?? "";
        if (string.IsNullOrWhiteSpace(caption))
            throw new InvalidOperationException("Enter a caption before saving.");
        string textPath = Path.ChangeExtension(path, ".txt");
        if (File.Exists(textPath) &&
            !string.Equals((await File.ReadAllTextAsync(textPath, cancellationToken)).Trim(),
                caption.Trim(), StringComparison.Ordinal))
        {
            var catalog = new DatasetCatalog(Path.GetDirectoryName(path)!);
            var pair = (await catalog.ListPairsAsync(cancellationToken)).FirstOrDefault(item =>
                string.Equals(item.MediaPath, path, StringComparison.Ordinal));
            if (pair?.ReviewStatus == DatasetReviewStatus.Reviewed)
                await catalog.SetReviewStatusAsync(path, DatasetReviewStatus.Pending, cancellationToken);
        }
        await CaptionFiles.SaveAsync(path, caption, cancellationToken);
        if (_export?.Path == path)
        {
            if (_initialDraft is not null &&
                !string.Equals(caption, _displayedGeneratedDraft, StringComparison.Ordinal))
                ClearDraftComparison();
            _savedCaption = caption;
            _captionDirty = !string.Equals(_overallBox.Text ?? "", _savedCaption, StringComparison.Ordinal);
            _discardEditsButton.IsEnabled = !_isBusy && _captionDirty;
            UpdateCaptionButtonLabel();
        }
    }

    private void ClearCaptions()
    {
        ClearDraftComparison();
        SetCaptionText("");
        _savedCaption = "";
        _captionDirty = false;
        _saveButton.IsEnabled = false;
        _discardEditsButton.IsEnabled = false;
        UpdateCaptionButtonLabel();
    }

    private void SetCaptionText(string caption)
    {
        _settingCaptionText = true;
        try { _overallBox.Text = caption; }
        finally { _settingCaptionText = false; }
        _captionDirty = !string.Equals(caption, _savedCaption, StringComparison.Ordinal);
        _discardEditsButton.IsEnabled = !_isBusy && _export is not null && _captionDirty;
        UpdateCompareDraftButton();
    }

    private void SetDraftComparison(string? initial, string? refined)
    {
        ClearDraftComparison();
        if (!string.IsNullOrWhiteSpace(initial) && !string.IsNullOrWhiteSpace(refined) &&
            !string.Equals(initial, refined, StringComparison.Ordinal))
        {
            _initialDraft = initial;
            _refinedDraft = refined;
            _displayedGeneratedDraft = refined;
        }
        UpdateCompareDraftButton();
    }

    private void ClearDraftComparison()
    {
        _initialDraft = null;
        _refinedDraft = null;
        _displayedGeneratedDraft = null;
        UpdateCompareDraftButton();
    }

    private void UpdateCompareDraftButton()
    {
        bool available = _initialDraft is not null && _refinedDraft is not null;
        _compareDraftButton.IsVisible = available;
        _compareDraftButton.IsEnabled = available && !_isBusy &&
            string.Equals(_overallBox.Text ?? "", _displayedGeneratedDraft, StringComparison.Ordinal);
        _compareDraftButton.Content = _displayedGeneratedDraft == _initialDraft
            ? "Show refined draft" : "Show initial draft";
    }

    private void UpdateCaptionButtonLabel() =>
        _captionButton.Content = _export is not null && !string.IsNullOrWhiteSpace(_savedCaption)
            ? "Try another draft" : "Caption exported clip";

    private bool CanReplaceCaption()
    {
        if (!_captionDirty || _export is null) return true;
        SetStatus("Save the current draft or choose Discard unsaved edits before opening, exporting, " +
                  "or captioning another clip.");
        return false;
    }

    private void StartJob(string message)
    {
        if (_isBusy || _jobCancellation is not null)
            throw new InvalidOperationException("A job is already running.");
        _jobCancellation = new CancellationTokenSource();
        SetBusy(true, message);
        _progress.Value = 0;
        _progress.IsIndeterminate = true;
    }

    private void FinishJob()
    {
        _jobCancellation?.Dispose();
        _jobCancellation = null;
        _progress.IsIndeterminate = false;
        SetBusy(false);
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _isBusy = busy;
        _cancelButton.IsEnabled = busy && _jobCancellation is not null;
        _batchFolder.IsEnabled = !busy;
        _chooseBatchFolder.IsEnabled = !busy;
        _batchSubfolders.IsEnabled = !busy;
        _batchRefine.IsEnabled = !busy &&
            _activeProject is { MediaPolicy: not ProjectMediaPolicy.VideoOnly };
        _startBatch.IsEnabled = !busy && !_modelTransition && _activeProject is not null;
        _cancelBatch.IsEnabled = busy && _batchRunning && _jobCancellation is not null;
        _refreshDataset.IsEnabled = !busy && _activeProject is not null;
        _datasetPairsList.IsEnabled = !busy;
        _datasetCaption.IsEnabled = !busy && _datasetSelected is not null;
        _finalDatasetPath.IsEnabled = !busy;
        _chooseFinalDatasetParent.IsEnabled = !busy;
        _finalizeDatasetButton.IsEnabled = !busy && _activeProject is not null;
        UpdateDatasetButtons();
        _openButton.IsEnabled = !busy;
        _startBox.IsEnabled = !busy;
        _endBox.IsEnabled = !busy;
        _setStartButton.IsEnabled = !busy;
        _setEndButton.IsEnabled = !busy;
        _bucketCombo.IsEnabled = !busy;
        _nearestBucketButton.IsEnabled = !busy;
        _customBucketWidth.IsEnabled = !busy;
        _customBucketHeight.IsEnabled = !busy;
        _addCustomBucket.IsEnabled = !busy && _activeProject is not null;
        _frameRateCombo.IsEnabled = !busy;
        _customFrameRate.IsEnabled = !busy;
        _addCustomFrameRate.IsEnabled = !busy && _activeProject is not null;
        _cropEditor.IsEnabled = !busy;
        _cropXSlider.IsEnabled = !busy;
        _cropYSlider.IsEnabled = !busy;
        _centerCropButton.IsEnabled = !busy;
        _projectCombo.IsEnabled = !busy;
        _newProjectName.IsEnabled = !busy;
        _newProjectMediaPolicy.IsEnabled = !busy;
        _newProjectVideoFormat.IsEnabled = !busy;
        _createProjectButton.IsEnabled = !busy;
        _exportButton.IsEnabled = !busy && _activeProject is not null;
        _captionButton.IsEnabled = !busy && !_modelTransition && _activeProject is not null;
        _overallBox.IsEnabled = !busy && _export is not null;
        UpdateCompareDraftButton();
        _saveButton.IsEnabled = !busy && _export is not null && !string.IsNullOrWhiteSpace(_overallBox.Text);
        _discardEditsButton.IsEnabled = !busy && _export is not null && _captionDirty;
        _modelCombo.IsEnabled = !busy && !_modelTransition && _worker?.Models.Count > 0;
        _unloadModelButton.IsEnabled = !busy && !_modelTransition && _worker?.LoadedModelId is not null;
        _refreshModelsButton.IsEnabled = !busy && !_modelTransition;
        _refineCaption.IsEnabled = !busy && _source?.HasVideo == true && _source.HasAudio &&
                                   _activeProject?.MediaPolicy != ProjectMediaPolicy.VideoOnly;
        _enableThinking.IsEnabled = !busy && !_modelTransition;
        _pythonPath.IsEnabled = !busy;
        _useDefaultSystem.IsEnabled = !busy;
        _systemPrompt.IsEnabled = !busy && _useDefaultSystem.IsChecked != true;
        _userPrompt.IsEnabled = !busy;
        _resetUserPromptButton.IsEnabled = !busy;
        _guidance.IsEnabled = !busy;
        UpdateGuidanceUi();
        if (message is not null) SetStatus(message);
        if (!busy) ResumeDownloadedModelRefresh();
    }

    private void SetStatus(string text) => _statusText.Text = text;
    private void ShowError(string text) => SetStatus("Error: " + text);

    private async Task ResetWorkerAsync()
    {
        // Multiple UI paths can reset concurrently (startup scan, cancel, window close).
        // Detach before awaiting so only one path disposes this worker.
        var worker = _worker;
        _worker = null;
        _workerPython = null;
        if (worker is not null) await worker.DisposeAsync();
        UpdateModelStatus();
    }

    private async Task CleanupAsync()
    {
        _jobCancellation?.Cancel();
        _frameCancellation?.Cancel();
        _datasetPreviewCancellation?.Cancel();
        await StopModelDownloadsAsync();
        _playTimer.Stop();
        await ResetWorkerAsync();
        _datasetPlayer?.Stop();
        _datasetVideoView.MediaPlayer = null;
        _datasetPlayer?.Dispose();
        _player?.Dispose();
        _libVlc?.Dispose();
        _frame?.Dispose();
        _waveform?.Dispose();
        _datasetFrame?.Dispose();
        _datasetPreviewCancellation?.Dispose();
    }

    private async Task CloseAfterCleanupAsync()
    {
        try { await CleanupAsync(); }
        finally
        {
            _closingAfterCleanup = true;
            Close();
        }
    }

    private static string FindProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "worker", "caption_worker.py")))
                return directory.FullName;
            directory = directory.Parent;
        }
        return AppContext.BaseDirectory;
    }

    private static string FindPython(string root)
    {
        string? configured = Environment.GetEnvironmentVariable("CAPTION_EXPRESS_PYTHON");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        string local = Path.Combine(root, ".venv", "bin", "python");
        if (File.Exists(local)) return local;
        return "python3";
    }

}
