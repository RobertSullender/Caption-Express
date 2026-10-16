using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CaptionExpress.Dataset;
using CaptionExpress.Media;

namespace CaptionExpress;

public partial class MainWindow
{
    private sealed record FrameRateChoice(string Label, double? Value)
    {
        public override string ToString() => Label;
    }

    private sealed record ProjectMediaPolicyChoice(string Label, ProjectMediaPolicy Policy)
    {
        public override string ToString() => Label;
    }

    private static readonly ProjectMediaPolicyChoice[] NewProjectMediaPolicyChoices =
    [
        new("Video + audio (joint training)", ProjectMediaPolicy.JointVideoAudio),
        new("Video only (remove audio)", ProjectMediaPolicy.VideoOnly)
    ];

    private ComboBox _projectCombo = null!;
    private TextBox _newProjectName = null!;
    private ComboBox _newProjectMediaPolicy = null!;
    private ComboBox _newProjectVideoFormat = null!;
    private Button _createProjectButton = null!;
    private TextBlock _projectSummary = null!;
    private TextBox _customBucketWidth = null!;
    private TextBox _customBucketHeight = null!;
    private Button _addCustomBucket = null!;
    private StackPanel _frameRateOptions = null!;
    private ComboBox _frameRateCombo = null!;
    private TextBox _customFrameRate = null!;
    private Button _addCustomFrameRate = null!;
    private TextBlock _finalDatasetDescription = null!;
    private ExportProjectStore _projectStore = null!;
    private ExportProject? _activeProject;
    private TrainingExportPreset? _activePreset;
    private bool _settingProject;
    private bool _projectTransition;

    private void InitializeProjectsUi()
    {
        _projectCombo = Get<ComboBox>("ProjectCombo");
        _newProjectName = Get<TextBox>("NewProjectNameBox");
        _newProjectMediaPolicy = Get<ComboBox>("NewProjectMediaPolicyCombo");
        _newProjectMediaPolicy.ItemsSource = NewProjectMediaPolicyChoices;
        _newProjectMediaPolicy.SelectedIndex = 0;
        _newProjectVideoFormat = Get<ComboBox>("NewProjectVideoFormatCombo");
        _newProjectVideoFormat.ItemsSource = VideoExportFormat.All;
        _newProjectVideoFormat.SelectedItem = VideoExportFormat.H264Mp4;
        _createProjectButton = Get<Button>("CreateProjectButton");
        _projectSummary = Get<TextBlock>("ProjectSummaryText");
        _customBucketWidth = Get<TextBox>("CustomBucketWidthBox");
        _customBucketHeight = Get<TextBox>("CustomBucketHeightBox");
        _addCustomBucket = Get<Button>("AddCustomBucketButton");
        _frameRateOptions = Get<StackPanel>("FrameRateOptions");
        _frameRateCombo = Get<ComboBox>("FrameRateCombo");
        _customFrameRate = Get<TextBox>("CustomFrameRateBox");
        _addCustomFrameRate = Get<Button>("AddCustomFrameRateButton");
        _finalDatasetDescription = Get<TextBlock>("FinalDatasetDescriptionText");
        _projectStore = new ExportProjectStore(Path.Combine(_projectRoot, "exports"));
        _frameRateCombo.ItemsSource = new[] { new FrameRateChoice("Choose a project first", null) };
        _frameRateCombo.SelectedIndex = 0;
    }

    private async Task LoadProjectsAsync(string? preferredName = null, bool chooseSoleProject = true)
    {
        try
        {
            var projects = await _projectStore.ListAsync();
            preferredName ??= await _projectStore.GetActiveNameAsync();
            _settingProject = true;
            try
            {
                _projectCombo.ItemsSource = projects;
                _projectCombo.SelectedItem = projects.FirstOrDefault(project =>
                    string.Equals(project.Name, preferredName, StringComparison.OrdinalIgnoreCase))
                    ?? (projects.Count == 1 && chooseSoleProject ? projects[0] : null);
            }
            finally { _settingProject = false; }
            if (_projectCombo.SelectedItem is ExportProject selected)
                await ActivateProjectAsync(selected);
            else
            {
                _activeProject = null;
                _activePreset = null;
                _exportDirectory.Text = "";
                _videoFormatText.Text = "Choose a project";
                _export = null;
                _exportSignature = null;
                _exportedFileText.Text = "No cropped clip exported yet.";
                ClearCaptions();
                _guidanceContext = null;
                UpdateGuidanceUi();
                _settingDatasetSelection = true;
                try
                {
                    _datasetPairsList.ItemsSource = Array.Empty<DatasetListEntry>();
                    _datasetPairsList.SelectedItem = null;
                }
                finally { _settingDatasetSelection = false; }
                ClearDatasetSelection();
                _datasetAllPairs = [];
                _datasetListSummary.Text = "";
                _datasetListedFolder = null;
                _finalDatasetPath.Text = DefaultFinalDatasetPath();
                RefreshBatchProjectUi();
                _projectSummary.Text = projects.Count == 0
                    ? "Create a named LTX 2.3 project before exporting."
                    : "Select an existing project above, or create a new one. No export target is active.";
                if (_projectStore.RecoveryWarning is { } recoveryWarning)
                    _projectSummary.Text += " " + recoveryWarning;
            }
        }
        catch (Exception error) { ShowError("Could not load projects: " + error.Message); }
        finally { if (!_isBusy) SetBusy(false); }
    }

    private async void CreateProjectClicked(object? sender, RoutedEventArgs e)
    {
        if (_isBusy || _projectTransition || !CanSwitchProject()) return;
        string name = _newProjectName.Text?.Trim() ?? "";
        try
        {
            _projectTransition = true;
            SetBusy(true, "Creating project…");
            var policy = (_newProjectMediaPolicy.SelectedItem as ProjectMediaPolicyChoice)?.Policy
                ?? ProjectMediaPolicy.JointVideoAudio;
            var format = _newProjectVideoFormat.SelectedItem as VideoExportFormat
                ?? throw new InvalidOperationException("Choose a video format for the new project.");
            if (!VideoExportFormat.All.Contains(format))
                throw new InvalidOperationException("Choose a supported video format for the new project.");
            var preset = TrainingExportPreset.Ltx23 with { DefaultVideoFormat = format };
            await _projectStore.CreateAsync(name, preset.Id, JsonSerializer.SerializeToElement(preset), policy);
            _newProjectName.Text = "";
            await LoadProjectsAsync(name);
        }
        catch (Exception error) { ShowError("Could not create project: " + error.Message); }
        finally { _projectTransition = false; SetBusy(false); }
    }

    private async void ProjectSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_settingProject) return;
        if (_projectTransition) { RestoreProjectSelection(); return; }
        if (_projectCombo.SelectedItem is not ExportProject selected ||
            string.Equals(selected.Name, _activeProject?.Name, StringComparison.Ordinal)) return;
        if (_isBusy || !CanSwitchProject())
        {
            RestoreProjectSelection();
            return;
        }
        try
        {
            _projectTransition = true;
            SetBusy(true, "Opening project…");
            await ActivateProjectAsync(selected);
        }
        catch (Exception error)
        {
            RestoreProjectSelection();
            ShowError("Could not open project: " + error.Message);
        }
        finally { _projectTransition = false; SetBusy(false); }
    }

    private bool CanSwitchProject()
    {
        if (_datasetLoading)
        {
            SetStatus("Wait for the current project's review list to finish loading.");
            return false;
        }
        if (!CanReplaceCaption()) return false;
        if (!_datasetCaptionDirty) return true;
        SetStatus("Save or discard the open dataset caption edit before switching projects.");
        return false;
    }

    private void RestoreProjectSelection()
    {
        _settingProject = true;
        try
        {
            _projectCombo.SelectedItem = (_projectCombo.ItemsSource as IEnumerable<ExportProject>)?
                .FirstOrDefault(project => project.Name == _activeProject?.Name);
        }
        finally { _settingProject = false; }
    }

    private async Task ActivateProjectAsync(ExportProject selected)
    {
        var project = await _projectStore.OpenAsync(selected.Name)
            ?? throw new DirectoryNotFoundException($"Project '{selected.Name}' is missing.");
        var preset = JsonSerializer.Deserialize<TrainingExportPreset>(project.EffectiveSettings.GetRawText())
            ?? throw new InvalidDataException("The project's export preset is missing.");
        var known = TrainingExportPreset.FindById(preset.Id);
        if (preset.Id != project.PresetId || known is null)
            throw new InvalidDataException("The project's export target is not supported by this build.");
        if (preset.SpatialMultiple != known.SpatialMultiple ||
            preset.TemporalFactor != known.TemporalFactor ||
            preset.MinimumFrames != known.MinimumFrames ||
            preset.Buckets is null || preset.Buckets.Count == 0 ||
            preset.FrameRates is null || preset.FrameRates.Count == 0)
            throw new InvalidDataException("The project's saved frame or bucket rules are invalid.");
        preset.ValidateFrameRate(preset.DefaultFrameRate);
        foreach (var bucket in preset.Buckets) preset.ValidateBucket(bucket);
        foreach (double rate in preset.FrameRates) preset.ValidateFrameRate(rate);
        var savedFormat = VideoExportFormat.All.FirstOrDefault(format =>
            format.Id == preset.DefaultVideoFormat?.Id)
            ?? throw new InvalidDataException("The project's saved video format is unsupported.");
        preset = preset with { Name = known.Name, DefaultVideoFormat = savedFormat };
        await _projectStore.SetActiveNameAsync(project.Name);

        _activeProject = project;
        _activePreset = preset;
        _exportDirectory.Text = project.DirectoryPath;
        _export = null;
        _exportSignature = null;
        _exportedFileText.Text = "No cropped clip exported yet.";
        ClearCaptions();
        _guidanceContext = null;
        UpdateGuidanceUi();
        _settingDatasetSelection = true;
        try
        {
            _datasetPairsList.ItemsSource = Array.Empty<DatasetListEntry>();
            _datasetPairsList.SelectedItem = null;
        }
        finally { _settingDatasetSelection = false; }
        ClearDatasetSelection();
        _datasetAllPairs = [];
        _datasetListSummary.Text = "";
        _datasetFilter = DatasetFilter.NeedsReview;
        _settingDatasetFilter = true;
        _datasetFilterCombo.SelectedItem = DatasetFilters[0];
        _settingDatasetFilter = false;
        _datasetListedFolder = null;
        _bucketCombo.ItemsSource = preset.Buckets;
        _bucketCombo.SelectedItem = _source?.HasVideo == true
            ? preset.NearestBucket(_source.Width, _source.Height) : null;
        _videoFormatText.Text = preset.DefaultVideoFormat.Name;
        RefreshBatchProjectUi();
        RebuildFrameRateChoices();
        _frameRateOptions.IsVisible = _source?.HasVideo == true;
        _finalDatasetPath.Text = DefaultFinalDatasetPath();
        string mediaDescription = project.MediaPolicy switch
        {
            ProjectMediaPolicy.JointVideoAudio =>
                "This project requires embedded audio in each finalized video for joint training. ",
            ProjectMediaPolicy.VideoOnly =>
                "Audio is removed from exported video and the training recipe skips audio. ",
            _ =>
                "Legacy mixed projects may include video without audio; the stock joint trainer skips those files. "
        };
        _finalDatasetDescription.Text = "Creates reviewed video/text pairs and dataset.jsonl " +
            "(video,caption) for LTX 2.3, plus a training recipe. " + mediaDescription +
            "Only copied working pairs are removed.";
        UpdateProjectSummary();
        UpdateLtxPlan();
        try { await RefreshDatasetPairsAsync(); }
        catch (Exception error) { _datasetStatus.Text = "Could not read this project's review list: " + error.Message; }
        SetStatus($"Project '{project.Name}' ready. Exports and review are scoped to this project.");
    }

    private void UpdateProjectSummary()
    {
        if (_activeProject is not { } project || _activePreset is not { } preset) return;
        string media = project.MediaPolicy switch
        {
            ProjectMediaPolicy.JointVideoAudio => "Video + audio required",
            ProjectMediaPolicy.VideoOnly => "Video only; audio removed from exports",
            _ => "Legacy mixed media; review stream compatibility"
        };
        _projectSummary.Text = $"{project.Name} · {preset.Name} · {media} · {preset.DefaultVideoFormat.Name} · {preset.SpatialMultiple}-pixel dimension grid · " +
            $"{preset.TemporalFactor}n+1 frames · {preset.Buckets.Count} crop buckets · suggested FPS " +
            $"{string.Join(", ", preset.FrameRates.Select(rate => rate.ToString("0.###", CultureInfo.InvariantCulture)))}. " +
            "Media type and video format are fixed for this project. Custom sizes and FPS can be saved while the project is empty. Export limits depend on the trainer and available memory.";
        if (_projectStore.RecoveryWarning is { } recoveryWarning)
            _projectSummary.Text += " " + recoveryWarning;
    }

    private void RebuildFrameRateChoices(double? selectRate = null)
    {
        if (_activePreset is null) return;
        string automaticRate = _activePreset.FrameRates.Count == 1
            ? $"{_activePreset.FrameRates[0].ToString("0.###", CultureInfo.InvariantCulture)} fps"
            : $"nearest of {string.Join("/", _activePreset.FrameRates.Select(rate => rate.ToString("0.###", CultureInfo.InvariantCulture)))} fps";
        var choices = new List<FrameRateChoice>
        {
            new($"Auto · {automaticRate}", null)
        };
        choices.AddRange(_activePreset.FrameRates.OrderBy(rate => rate)
            .Select(rate => new FrameRateChoice($"{rate:0.###} fps", rate)));
        _frameRateCombo.ItemsSource = choices;
        _frameRateCombo.SelectedItem = choices.FirstOrDefault(choice => choice.Value == selectRate) ?? choices[0];
        UpdateLtxPlan();
    }

    private double ResolveFrameRate() =>
        (_frameRateCombo.SelectedItem as FrameRateChoice)?.Value ??
        (_activePreset ?? throw new InvalidOperationException("Choose a project first."))
            .ChooseFrameRate(_source?.FrameRate);

    private void FrameRateSelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateLtxPlan();

    private async void AddCustomBucketClicked(object? sender, RoutedEventArgs e)
    {
        if (_isBusy || _projectTransition || _activeProject is null || _activePreset is null) return;
        if (!int.TryParse(_customBucketWidth.Text, out int width) ||
            !int.TryParse(_customBucketHeight.Text, out int height))
        { ShowError("Enter integer width and height for the custom bucket."); return; }
        var bucket = new Bucket($"{width}×{height}", width, height);
        try
        {
            _activePreset.ValidateBucket(bucket);
            if (_activePreset.Buckets.Any(item => item.Width == width && item.Height == height))
                throw new ArgumentException("That bucket is already in this project.");
            var updated = _activePreset with { Buckets = [.. _activePreset.Buckets, bucket] };
            _projectTransition = true;
            SetBusy(true, "Saving project bucket…");
            _activeProject = await _projectStore.UpdateEffectiveSettingsAsync(_activeProject.Name,
                JsonSerializer.SerializeToElement(updated));
            _activePreset = updated;
            _bucketCombo.ItemsSource = updated.Buckets;
            _bucketCombo.SelectedItem = bucket;
            RefreshBatchProjectUi();
            UpdateProjectSummary();
            SetStatus($"Added {width}×{height} to project '{_activeProject.Name}'.");
        }
        catch (Exception error) { ShowError(error.Message); }
        finally { _projectTransition = false; if (_isBusy) SetBusy(false); }
    }

    private async void AddCustomFrameRateClicked(object? sender, RoutedEventArgs e)
    {
        if (_isBusy || _projectTransition || _activeProject is null || _activePreset is null) return;
        if (!double.TryParse(_customFrameRate.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double rate))
        { ShowError("Enter frame rate using a decimal point, for example 30."); return; }
        try
        {
            _activePreset.ValidateFrameRate(rate);
            if (_activePreset.FrameRates.Contains(rate))
                throw new ArgumentException("That frame rate is already in this project.");
            var updated = _activePreset with { FrameRates = [.. _activePreset.FrameRates, rate] };
            _projectTransition = true;
            SetBusy(true, "Saving project frame rate…");
            _activeProject = await _projectStore.UpdateEffectiveSettingsAsync(_activeProject.Name,
                JsonSerializer.SerializeToElement(updated));
            _activePreset = updated;
            RebuildFrameRateChoices(rate);
            RefreshBatchProjectUi();
            UpdateProjectSummary();
            SetStatus($"Added {rate:0.###} fps to project '{_activeProject.Name}'.");
        }
        catch (Exception error) { ShowError(error.Message); }
        finally { _projectTransition = false; if (_isBusy) SetBusy(false); }
    }
}
