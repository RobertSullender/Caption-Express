using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CaptionExpress.Caption;
using CaptionExpress.Dataset;
using CaptionExpress.Media;

namespace CaptionExpress;

public partial class MainWindow
{
    private static readonly HashSet<string> BatchMediaExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".mkv", ".webm", ".avi", ".mp3", ".wav", ".flac", ".m4a", ".ogg" };

    private TextBox _batchFolder = null!;
    private Button _chooseBatchFolder = null!;
    private CheckBox _batchSubfolders = null!;
    private TextBlock _batchVideoFormatText = null!;
    private TextBlock _batchOutput = null!;
    private TextBlock _batchTiming = null!;
    private CheckBox _batchRefine = null!;
    private Button _startBatch = null!;
    private Button _cancelBatch = null!;
    private TextBlock _batchStatus = null!;
    private ProgressBar _batchProgress = null!;
    private ListBox _batchLogList = null!;
    private readonly ObservableCollection<string> _batchLog = [];
    private bool _batchRunning;

    private void InitializeBatchUi()
    {
        _batchFolder = Get<TextBox>("BatchFolderBox");
        _chooseBatchFolder = Get<Button>("ChooseBatchFolderButton");
        _batchSubfolders = Get<CheckBox>("BatchSubfoldersCheck");
        _batchVideoFormatText = Get<TextBlock>("BatchVideoFormatText");
        _batchOutput = Get<TextBlock>("BatchOutputText");
        _batchTiming = Get<TextBlock>("BatchTimingText");
        _batchRefine = Get<CheckBox>("BatchRefineCheck");
        _startBatch = Get<Button>("StartBatchButton");
        _cancelBatch = Get<Button>("CancelBatchButton");
        _batchStatus = Get<TextBlock>("BatchStatusText");
        _batchProgress = Get<ProgressBar>("BatchProgress");
        _batchLogList = Get<ListBox>("BatchLogList");
        _batchLogList.ItemsSource = _batchLog;
        RefreshBatchProjectUi();
    }

    private void RefreshBatchProjectUi()
    {
        if (_batchOutput is null || _batchTiming is null || _batchVideoFormatText is null) return;
        if (_activeProject is not { } project || _activePreset is not { } preset)
        {
            _batchOutput.Text = "Choose or create a project under One clip before starting a batch.";
            _batchTiming.Text = "Select a project to see its video export and media rules.";
            _batchVideoFormatText.Text = "Choose a project";
            if (_batchRefine is not null) _batchRefine.IsEnabled = false;
            return;
        }

        _batchOutput.Text = $"Project: {project.Name} · Working exports: {project.DirectoryPath}";
        string automaticRates = preset.FrameRates.Count == 1
            ? $"automatic {preset.FrameRates[0].ToString("0.###", CultureInfo.InvariantCulture)} fps"
            : $"nearest source rate among {string.Join(" / ", preset.FrameRates.Select(rate => rate.ToString("0.###", CultureInfo.InvariantCulture)))} fps";
        _batchTiming.Text = $"{preset.Name}: {automaticRates}; " +
            $"{preset.TemporalFactor}n+1 frames; nearest preset crop bucket (multiples of {preset.SpatialMultiple}). " +
            (project.MediaPolicy switch
            {
                ProjectMediaPolicy.JointVideoAudio =>
                    "Video without embedded audio and audio-only files are skipped before export.",
                ProjectMediaPolicy.VideoOnly =>
                    "Audio-only files are skipped. Embedded audio is removed from exported video; captions describe visuals only.",
                _ => "Legacy mixed project: audio-only files export as FLAC."
            });
        if (_batchRefine is not null)
            _batchRefine.IsEnabled = project.MediaPolicy != ProjectMediaPolicy.VideoOnly;
        _batchVideoFormatText.Text = preset.DefaultVideoFormat.Name;
    }

    private async void ChooseBatchFolderClicked(object? sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the source folder for a sequential caption run",
            AllowMultiple = false
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            _batchFolder.Text = path;
    }

    private void CancelBatchClicked(object? sender, RoutedEventArgs e) => _jobCancellation?.Cancel();

    private async void StartBatchClicked(object? sender, RoutedEventArgs e)
    {
        if (_isBusy || _modelTransition || !CanReplaceCaption()) return;
        string inputText = _batchFolder.Text?.Trim() ?? "";
        if (_activeProject is not { } project || _activePreset is not { } preset)
        {
            _batchStatus.Text = "Choose or create a project under One clip before starting a batch.";
            return;
        }
        VideoExportFormat videoFormat = preset.DefaultVideoFormat;
        if (inputText.Length == 0 || !Directory.Exists(inputText))
        {
            _batchStatus.Text = "Choose an existing source folder.";
            return;
        }
        string inputRoot = Path.GetFullPath(inputText);
        string outputRoot = Path.GetFullPath(project.DirectoryPath);
        string exportsRoot = Path.GetDirectoryName(outputRoot) ?? outputRoot;
        if (IsWithinDirectory(inputRoot, exportsRoot) ||
            (File.GetAttributes(inputRoot) & FileAttributes.ReparsePoint) != 0)
        {
            _batchStatus.Text = "Choose a source folder outside the exports root.";
            return;
        }

        _batchRunning = true;
        bool statusRequestPending = false;
        try
        {
            StartJob("Preparing folder run…");
            _batchProgress.Value = 0;
            _batchLog.Clear();
            var token = _jobCancellation!.Token;
            string[] sources = EnumerateBatchSources(inputRoot, exportsRoot,
                    _batchSubfolders.IsChecked == true, token)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (sources.Length == 0)
            {
                _batchStatus.Text = "No supported media files were found in that folder.";
                return;
            }

            var catalog = new DatasetCatalog(outputRoot);
            string? requestedModelId = (_modelCombo.SelectedItem as WorkerModel)?.Id;
            WorkerClient worker = await EnsureWorkerAsync(token);
            statusRequestPending = true;
            await worker.RequestAsync(new { command = "status" }, cancellationToken: token);
            statusRequestPending = false;
            ApplyModels();
            string? modelId = (_modelCombo.SelectedItem as WorkerModel)?.Id;
            if (requestedModelId is not null && requestedModelId != modelId)
                throw new InvalidOperationException("The selected Gemma model is no longer available. Choose an available model and retry.");
            if (modelId is null)
                throw new InvalidOperationException("Install a Gemma 4 E2B/E4B GGUF with its matching mmproj, then refresh models.");
            string userPrompt = _userPrompt.Text ?? "";
            if (string.IsNullOrWhiteSpace(userPrompt))
                throw new InvalidOperationException("Enter a caption user prompt under One clip → Model and prompts.");
            string? systemPrompt = _useDefaultSystem.IsChecked == true ? null : _systemPrompt.Text;
            if (_useDefaultSystem.IsChecked != true && string.IsNullOrWhiteSpace(systemPrompt))
                throw new InvalidOperationException("Enter a custom system prompt or use the model chat template without one.");
            bool thinking = _enableThinking.IsChecked == true;
            bool refine = _batchRefine.IsChecked == true &&
                project.MediaPolicy != ProjectMediaPolicy.VideoOnly;
            bool includeAudio = project.MediaPolicy != ProjectMediaPolicy.VideoOnly;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int done = 0, duplicates = 0, noVideo = 0, noAudio = 0, failed = 0;
            for (int index = 0; index < sources.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                string source = sources[index];
                string label = Path.GetFileName(source);
                string? activeHash = null;
                bool captionRequestPending = false;
                _batchStatus.Text = $"{index + 1}/{sources.Length}: checking {label}";
                try
                {
                    // Probe first: ineligible media should never be hashed, exported, added
                    // to the catalog, or sent to the caption model.
                    MediaInfo media = await MediaProbe.ReadAsync(source, token);
                    string? rejection = ProjectMediaPolicyRules.GetRejection(project.MediaPolicy,
                        media.HasVideo, media.HasAudio);
                    if (rejection is not null)
                    {
                        if (!media.HasVideo) noVideo++;
                        else noAudio++;
                        AddBatchLog($"Skipped by project media type: {label} — {rejection}");
                        continue;
                    }
                    string hash = await catalog.ComputeSourceHashAsync(source, token);
                    if (!seen.Add(hash))
                    {
                        duplicates++;
                        AddBatchLog($"Skipped exact duplicate in folder: {label}");
                        continue;
                    }
                    activeHash = hash;
                    if (await catalog.ContainsSourceHashAsync(hash, token))
                    {
                        duplicates++;
                        AddBatchLog($"Skipped previously processed duplicate: {label}");
                        continue;
                    }
                    DatasetPair? incomplete = await catalog.FindIncompleteExportByHashAsync(hash, token, videoFormat.Id);
                    ExportResult export;
                    if (incomplete is not null)
                    {
                        var existing = await MediaProbe.ReadAsync(incomplete.MediaPath, token);
                        if (ProjectMediaPolicyRules.GetRejection(project.MediaPolicy,
                                existing.HasVideo, existing.HasAudio) is { } existingRejection)
                            throw new InvalidDataException($"Incomplete export does not match project media type: {existingRejection}");
                        if (!includeAudio && existing.HasAudio)
                            throw new InvalidDataException("Incomplete video-only export still has an audio stream. " +
                                "Remove that working export before retrying this source.");
                        ValidateBatchMedia(existing, modelId, userPrompt, refine);
                        int? frameCount = existing.HasVideo && existing.FrameRate is > 0
                            ? (int)Math.Round(existing.Duration * existing.FrameRate.Value)
                            : null;
                        export = new ExportResult(incomplete.MediaPath, existing.Duration, null, 0.5, 0.5,
                            frameCount, existing.FrameRate);
                        AddBatchLog($"Retrying incomplete export: {Path.GetFileName(export.Path)}");
                    }
                    else
                    {
                        ValidateBatchMedia(media, modelId, userPrompt, refine, includeAudio);
                        Bucket? bucket = media.HasVideo ? preset.NearestBucket(media.Width, media.Height) : null;
                        double frameRate = preset.ChooseFrameRate(media.FrameRate);
                        _batchStatus.Text = $"{index + 1}/{sources.Length}: exporting {label}";
                        export = await MediaExporter.ExportAsync(media, outputRoot, 0, media.Duration,
                            bucket, 0.5, 0.5, token, videoFormat, preset, frameRate,
                            includeAudio: includeAudio);
                        await catalog.RecordExportAsync(export.Path, hash, token, batchExport: true,
                            videoFormatId: videoFormat.Id);
                    }
                    if (export.FrameCount is { } frames && export.FrameRate is { } fps)
                        AddBatchLog($"Export: {Path.GetFileName(export.Path)} · {videoFormat.Name} · " +
                            $"{frames} frames · {fps.ToString("0.###", CultureInfo.InvariantCulture)} fps · " +
                            $"{export.Duration.ToString("0.###", CultureInfo.InvariantCulture)} s");
                    else
                        AddBatchLog($"Export: {Path.GetFileName(export.Path)} · " +
                            $"{export.Duration.ToString("0.###", CultureInfo.InvariantCulture)} s (audio only)");
                    worker = await EnsureWorkerAsync(token);
                    _batchStatus.Text = $"{index + 1}/{sources.Length}: captioning {label}";
                    captionRequestPending = true;
                    var result = await worker.RequestAsync(new
                    {
                        command = "caption",
                        path = export.Path,
                        model_id = modelId,
                        enable_thinking = thinking,
                        refine_caption = refine,
                        system_prompt = systemPrompt,
                        user_prompt = userPrompt
                    }, cancellationToken: token);
                    captionRequestPending = false;
                    UpdateModelStatus();
                    string caption = result.GetProperty("caption").GetString() ?? "";
                    await CaptionFiles.SaveAsync(export.Path, caption, token);
                    done++;
                    AddBatchLog($"Ready for review: {Path.GetFileName(export.Path)} ← {label}");
                    if (result.TryGetProperty("audio_warning", out var audioIssue) &&
                        audioIssue.ValueKind == JsonValueKind.String)
                        AddBatchLog($"Audio review needed for {Path.GetFileName(export.Path)}: {audioIssue.GetString()}");
                    if (result.TryGetProperty("refinement_skipped_reason", out var refinementSkipped) &&
                        refinementSkipped.ValueKind == JsonValueKind.String)
                        AddBatchLog($"Refinement skipped for {Path.GetFileName(export.Path)}: {refinementSkipped.GetString()}");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    if (activeHash is not null) seen.Remove(activeHash);
                    failed++;
                    AddBatchLog($"Failed: {label} — {error.Message}");
                    if (captionRequestPending) await ResetWorkerAsync();
                }
                finally
                {
                    _batchProgress.Value = 100.0 * (index + 1) / sources.Length;
                    _batchStatus.Text = $"{index + 1}/{sources.Length} checked · {done} ready · " +
                        $"{duplicates} duplicate · {noVideo} no video · {noAudio} no audio · {failed} failed";
                }
            }
            _batchStatus.Text = $"Folder run complete: {done} pairs ready to review, " +
                $"{duplicates} duplicates skipped, {noVideo} without video skipped, " +
                $"{noAudio} without audio skipped, {failed} failed.";
            if (!_datasetCaptionDirty) await RefreshDatasetPairsAsync(token);
            else _datasetStatus.Text = "Working pairs changed. Save or discard the current edit, then refresh pairs.";
        }
        catch (OperationCanceledException)
        {
            _batchStatus.Text = "Folder run canceled. Completed pairs remain in working exports.";
            await ResetWorkerAsync();
            if (!_datasetCaptionDirty)
            {
                try { await RefreshDatasetPairsAsync(); }
                catch (Exception error) { _datasetStatus.Text = "Could not refresh working pairs: " + error.Message; }
            }
        }
        catch (Exception error)
        {
            _batchStatus.Text = "Folder run stopped: " + error.Message;
            if (statusRequestPending || _worker is { IsAlive: false }) await ResetWorkerAsync();
        }
        finally
        {
            _batchRunning = false;
            FinishJob();
            await ModelConfigurationChangedAsync();
            UpdateModelStatus();
            SetStatus(_batchStatus.Text ?? "Folder run finished.");
        }
    }

    private void AddBatchLog(string text)
    {
        _batchLog.Add(text);
        _batchLogList.ScrollIntoView(_batchLog.Count - 1);
    }

    private static bool IsWithinDirectory(string file, string directory)
    {
        string relative = Path.GetRelativePath(directory, file);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." &&
                                    !relative.StartsWith(".." + Path.DirectorySeparatorChar,
                                        StringComparison.Ordinal));
    }

    private static IEnumerable<string> EnumerateBatchSources(string inputRoot, string exportsRoot,
        bool recurse, CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        var directories = new Stack<string>();
        directories.Push(inputRoot);
        while (directories.TryPop(out string? directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsWithinDirectory(directory, exportsRoot)) continue;
            foreach (string file in Directory.EnumerateFiles(directory, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (BatchMediaExtensions.Contains(Path.GetExtension(file)) &&
                    !IsWithinDirectory(file, exportsRoot))
                    yield return file;
            }
            if (!recurse) continue;
            foreach (string child in Directory.EnumerateDirectories(directory, "*", options))
                if (!IsWithinDirectory(child, exportsRoot))
                    directories.Push(child);
        }
    }

    private static void ValidateBatchMedia(MediaInfo media, string? modelId,
        string userPrompt, bool refine, bool includeAudio = true)
    {
        bool captionAudio = media.HasAudio && includeAudio;
        if (!media.HasVideo && !captionAudio)
            throw new InvalidOperationException("This file has no supported video or audio stream.");
        if (modelId is null)
            throw new InvalidOperationException("Select an installed Gemma 4 model under One clip → Model and prompts.");
        if (media.HasVideo && string.IsNullOrWhiteSpace(PromptForMode(userPrompt, "video")))
            throw new InvalidOperationException("Enter instructions for the video caption.");
        if (captionAudio && string.IsNullOrWhiteSpace(PromptForMode(userPrompt,
                media.HasVideo ? "soundtrack" : "audio_only")))
            throw new InvalidOperationException("Enter instructions for the audio caption.");
        if (refine && media.HasVideo && captionAudio &&
            string.IsNullOrWhiteSpace(PromptForMode(userPrompt, "refine")))
            throw new InvalidOperationException("Enter refinement instructions or turn off Refine combined caption.");
    }
}
