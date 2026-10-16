using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace CaptionExpress;

public partial class MainWindow
{
    private readonly Dictionary<string, CheckBox> _modelDownloadChoices = [];
    private Button _downloadModelsButton = null!;
    private Button _pauseModelDownloadsButton = null!;
    private TextBlock _modelDownloadStatus = null!;
    private TextBlock _downloadSummary = null!;
    private ProgressBar _modelDownloadProgress = null!;
    private CancellationTokenSource? _modelDownloadCancellation;
    private Task? _modelDownloadTask;
    private bool _modelDownloadRunning;
    private bool _pendingDownloadedModelRefresh;
    private bool _downloadedModelRefreshRunning;
    private string? _lastModelDownloadError;
    private readonly HashSet<string> _completedModelDownloads = [];

    private static readonly Dictionary<string, string> DownloadChoiceLabels = new()
    {
        ["stock-e2b"] = "Stock E2B",
        ["stock-e4b"] = "Stock E4B",
        ["uncensored-e2b"] = "Uncensored E2B",
        ["uncensored-e4b"] = "Uncensored E4B",
    };

    // The downloader publishes each pair atomically in its own repository folder.
    // Check only these app-owned copies; a matching model in LM Studio or ComfyUI
    // does not mean that the requested download is already here.
    private sealed record DownloadChoiceSpec(string Label, string Repository,
        string Weight, long WeightBytes, string Projector, long ProjectorBytes);

    private static readonly Dictionary<string, DownloadChoiceSpec> DownloadChoiceSpecs = new()
    {
        ["stock-e2b"] = new("Stock E2B · Q8_0", "lmstudio-community/gemma-4-E2B-it-GGUF",
            "gemma-4-E2B-it-Q8_0.gguf", 4967497152,
            "mmproj-gemma-4-E2B-it-BF16.gguf", 986833248),
        ["stock-e4b"] = new("Stock E4B · Q8_0", "lmstudio-community/gemma-4-E4B-it-GGUF",
            "gemma-4-E4B-it-Q8_0.gguf", 8031240000,
            "mmproj-gemma-4-E4B-it-BF16.gguf", 991551840),
        ["uncensored-e2b"] = new("Uncensored E2B · Q8_K_P", "HauhauCS/Gemma-4-E2B-Uncensored-HauhauCS-Aggressive",
            "Gemma-4-E2B-Uncensored-HauhauCS-Aggressive-Q8_K_P.gguf", 5036057536,
            "mmproj-Gemma-4-E2B-Uncensored-HauhauCS-Aggressive-f16.gguf", 985570240),
        ["uncensored-e4b"] = new("Uncensored E4B · Q8_K_P", "HauhauCS/Gemma-4-E4B-Uncensored-HauhauCS-Aggressive",
            "Gemma-4-E4B-Uncensored-HauhauCS-Aggressive-Q8_K_P.gguf", 8133226464,
            "mmproj-Gemma-4-E4B-Uncensored-HauhauCS-Aggressive-f16.gguf", 990288832),
    };

    private enum ModelInstallState { NotInstalled, Incomplete, Installed }

    private ModelInstallState GetModelInstallState(DownloadChoiceSpec spec)
    {
        string directory = Path.Combine(_projectRoot, "models", spec.Repository);
        string weight = Path.Combine(directory, spec.Weight);
        string projector = Path.Combine(directory, spec.Projector);
        // The downloader refuses a linked destination directory. Do not call
        // such a pair installed merely because its target contains GGUF files.
        try
        {
            if (new DirectoryInfo(directory).LinkTarget is not null)
                return ModelInstallState.Incomplete;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return ModelInstallState.Incomplete;
        }
        if (HasExpectedGguf(weight, spec.WeightBytes) &&
            HasExpectedGguf(projector, spec.ProjectorBytes))
            return ModelInstallState.Installed;
        return Directory.Exists(directory) || File.Exists(weight) || File.Exists(projector)
            ? ModelInstallState.Incomplete : ModelInstallState.NotInstalled;
    }

    private static bool HasExpectedGguf(string path, long expectedBytes)
    {
        try
        {
            var file = new FileInfo(path);
            if (file.LinkTarget is not null || file.Length != expectedBytes) return false;
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[4];
            return stream.Read(header) == header.Length &&
                   header[0] == (byte)'G' && header[1] == (byte)'G' &&
                   header[2] == (byte)'U' && header[3] == (byte)'F';
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    private void RefreshModelInstallStatus()
    {
        int installed = 0;
        int incomplete = 0;
        foreach (var (id, checkBox) in _modelDownloadChoices)
        {
            DownloadChoiceSpec spec = DownloadChoiceSpecs[id];
            ModelInstallState state = GetModelInstallState(spec);
            if (state == ModelInstallState.Installed) installed++;
            if (state == ModelInstallState.Incomplete) incomplete++;
            string stateText = state switch
            {
                ModelInstallState.Installed => "Installed in app",
                ModelInstallState.Incomplete => "Incomplete — repair available",
                _ => "Not installed — download available",
            };
            checkBox.Content = $"{spec.Label}\n{stateText}";
            ToolTip.SetTip(checkBox, state == ModelInstallState.Installed
                ? "Model and matching projector are in this app's models folder. Select to verify their SHA256 checksums."
                : state == ModelInstallState.Incomplete
                    ? "This model pair is incomplete in the app's models folder. Select to repair or resume its download."
                    : "Select to download the model and its matching projector into this app's models folder.");
        }
        if (!_modelDownloadRunning)
        {
            _downloadSummary.Text = $"App models: {installed} of {DownloadChoiceSpecs.Count} installed.";
            _modelDownloadStatus.Text = installed == DownloadChoiceSpecs.Count
                ? "All four model/projector pairs are installed in this app. Select any to verify their files."
                : incomplete > 0
                    ? $"{installed} of 4 pairs installed; {incomplete} incomplete. Select missing or incomplete pairs to download or repair."
                    : $"{installed} of 4 pairs installed. Select any missing pair to download it.";
        }
        UpdateModelDownloadControls();
    }

    private void InitializeModelDownloadUi()
    {
        _modelDownloadChoices.Add("stock-e2b", Get<CheckBox>("StockE2BDownloadCheck"));
        _modelDownloadChoices.Add("stock-e4b", Get<CheckBox>("StockE4BDownloadCheck"));
        _modelDownloadChoices.Add("uncensored-e2b", Get<CheckBox>("UncensoredE2BDownloadCheck"));
        _modelDownloadChoices.Add("uncensored-e4b", Get<CheckBox>("UncensoredE4BDownloadCheck"));
        _downloadModelsButton = Get<Button>("DownloadModelsButton");
        _pauseModelDownloadsButton = Get<Button>("PauseModelDownloadsButton");
        _modelDownloadStatus = Get<TextBlock>("ModelDownloadStatusText");
        _downloadSummary = Get<TextBlock>("DownloadSummaryText");
        _modelDownloadProgress = Get<ProgressBar>("ModelDownloadProgress");
        foreach (var choice in _modelDownloadChoices.Values)
            choice.IsCheckedChanged += (_, _) => UpdateModelDownloadControls();
        RefreshModelInstallStatus();
    }

    private void UpdateModelDownloadControls()
    {
        bool running = _modelDownloadRunning;
        foreach (var choice in _modelDownloadChoices.Values) choice.IsEnabled = !running;
        string[] chosen = _modelDownloadChoices.Where(pair => pair.Value.IsChecked == true)
            .Select(pair => pair.Key).ToArray();
        _downloadModelsButton.IsEnabled = !running && chosen.Length > 0;
        _downloadModelsButton.Content = chosen.Length > 0 &&
            chosen.All(id => GetModelInstallState(DownloadChoiceSpecs[id]) == ModelInstallState.Installed)
                ? "Verify selected models" : "Download selected models";
        _pauseModelDownloadsButton.IsEnabled = running;
    }

    private void DownloadModelsClicked(object? sender, RoutedEventArgs e)
    {
        if (_modelDownloadRunning) return;
        string[] chosen = _modelDownloadChoices.Where(pair => pair.Value.IsChecked == true)
            .Select(pair => pair.Key).ToArray();
        if (chosen.Length == 0) return;
        _completedModelDownloads.Clear();
        _lastModelDownloadError = null;
        _modelDownloadCancellation = new CancellationTokenSource();
        _modelDownloadRunning = true;
        _modelDownloadProgress.Value = 0;
        _modelDownloadStatus.Text = "Preparing selected model downloads…";
        _downloadSummary.Text = $"Model downloads: preparing {chosen.Length} selected pair(s)…";
        _modelDownloadTask = RunModelDownloadsAsync(chosen, _modelDownloadCancellation.Token);
        UpdateModelDownloadControls();
    }

    private void PauseModelDownloadsClicked(object? sender, RoutedEventArgs e)
    {
        _modelDownloadCancellation?.Cancel();
        _modelDownloadStatus.Text = "Pausing downloads. Completed files are kept; the current file can resume later.";
        _downloadSummary.Text = "Model downloads: pausing…";
        _pauseModelDownloadsButton.IsEnabled = false;
    }

    private async Task RunModelDownloadsAsync(string[] chosen, CancellationToken cancellationToken)
    {
        string script = Path.Combine(_projectRoot, "scripts", "download-gemma-models.py");
        try
        {
            if (!File.Exists(script)) throw new FileNotFoundException("Model download script missing.", script);
            var start = new ProcessStartInfo
            {
                FileName = _pythonPath.Text?.Trim() is { Length: > 0 } python ? python : "python3",
                WorkingDirectory = _projectRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add(script);
            start.ArgumentList.Add("--json-progress");
            foreach (string id in chosen)
            {
                start.ArgumentList.Add("--model-id");
                start.ArgumentList.Add(id);
            }
            using var process = new Process { StartInfo = start };
            if (!process.Start()) throw new InvalidOperationException("Could not start the model downloader.");
            using var stop = cancellationToken.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            });
            Task<string> errors = process.StandardError.ReadToEndAsync();
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync()) is not null)
            {
                ModelDownloadUpdate? update = ParseModelDownloadEvent(line);
                if (update is not null)
                    await Dispatcher.UIThread.InvokeAsync(() => ApplyModelDownloadEvent(update.Value));
            }
            await process.WaitForExitAsync(cancellationToken);
            string errorText = (await errors).Trim();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(_lastModelDownloadError ?? (errorText.Length > 0
                    ? errorText[^Math.Min(500, errorText.Length)..]
                    : $"Downloader exited with code {process.ExitCode}."));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _modelDownloadProgress.Value = 100;
                _modelDownloadStatus.Text = $"Verified all {chosen.Length} selected model/projector pair(s) in the app's models folder.";
                _downloadSummary.Text = $"Model downloads: {chosen.Length} selected pair(s) ready.";
                foreach (string id in chosen) _modelDownloadChoices[id].IsChecked = false;
                ScheduleDownloadedModelRefresh();
            });
        }
        catch (OperationCanceledException)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _modelDownloadProgress.IsIndeterminate = false;
                _modelDownloadStatus.Text = "Downloads paused. Choose the same models and click Download selected models to resume.";
                _downloadSummary.Text = "Model downloads: paused; resumable files kept.";
            });
        }
        catch (Exception error)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _modelDownloadProgress.IsIndeterminate = false;
                _modelDownloadStatus.Text = "Download failed: " + error.Message + " Select the models and retry to resume.";
                _downloadSummary.Text = "Model downloads: failed; see Model and prompts for details.";
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _modelDownloadCancellation?.Dispose();
                _modelDownloadCancellation = null;
                _modelDownloadTask = null;
                _modelDownloadRunning = false;
                UpdateModelDownloadControls();
            });
        }
    }

    private readonly record struct ModelDownloadUpdate(string Event, string Model, string File,
        long Bytes, long Total, string Message);

    private static ModelDownloadUpdate? ParseModelDownloadEvent(string line)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement item = document.RootElement;
            string GetString(string name) => item.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
            long GetNumber(string name) => item.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) ? number : 0;
            return new ModelDownloadUpdate(GetString("event"), GetString("model"), GetString("file"),
                GetNumber("bytes"), GetNumber("total"), GetString("message"));
        }
        catch (JsonException) { return null; }
    }

    private void ApplyModelDownloadEvent(ModelDownloadUpdate update)
    {
        string model = DownloadChoiceLabels.GetValueOrDefault(update.Model, update.Model);
        switch (update.Event)
        {
            case "start":
                _modelDownloadProgress.IsIndeterminate = false;
                _modelDownloadStatus.Text = update.File.Length > 0
                    ? $"Preparing {model}: {update.File}"
                    : $"Preparing {model} and its matching projector…";
                _downloadSummary.Text = $"Model downloads: preparing {model}…";
                break;
            case "progress":
                _modelDownloadProgress.IsIndeterminate = false;
                if (update.Total > 0)
                {
                    double percent = Math.Clamp(100.0 * update.Bytes / update.Total, 0, 100);
                    _modelDownloadProgress.Value = percent;
                    _modelDownloadStatus.Text = $"Downloading {model}: {update.File} · {percent:0.0}% " +
                        $"({update.Bytes / 1e9:0.00} / {update.Total / 1e9:0.00} GB)";
                    _downloadSummary.Text = $"Model downloads: {model} {percent:0.0}% of current file.";
                }
                break;
            case "verify":
                _modelDownloadProgress.IsIndeterminate = true;
                _modelDownloadStatus.Text = $"Verifying {model}: {update.File}";
                _downloadSummary.Text = $"Model downloads: verifying {model}…";
                break;
            case "complete":
                _modelDownloadProgress.IsIndeterminate = false;
                _completedModelDownloads.Add(update.Model);
                _modelDownloadStatus.Text = $"Verified {model} and its matching projector.";
                _downloadSummary.Text = $"Model downloads: {_completedModelDownloads.Count} selected pair(s) ready.";
                RefreshModelInstallStatus();
                ScheduleDownloadedModelRefresh();
                break;
            case "error":
                _modelDownloadProgress.IsIndeterminate = false;
                _lastModelDownloadError = update.Message;
                _modelDownloadStatus.Text = $"Download failed for {model}: {update.Message}";
                _downloadSummary.Text = "Model downloads: failed; see Model and prompts for details.";
                break;
            case "paused":
                _modelDownloadProgress.IsIndeterminate = false;
                _downloadSummary.Text = "Model downloads: paused; resumable files kept.";
                break;
        }
    }

    private void ScheduleDownloadedModelRefresh()
    {
        _pendingDownloadedModelRefresh = true;
        ResumeDownloadedModelRefresh();
    }

    private void ResumeDownloadedModelRefresh()
    {
        if (!_isBusy && !_modelTransition && !_downloadedModelRefreshRunning)
            if (_pendingDownloadedModelRefresh)
                Dispatcher.UIThread.Post(() => _ = RefreshDownloadedModelsAsync());
    }

    private async Task RefreshDownloadedModelsAsync()
    {
        if (!_pendingDownloadedModelRefresh || _isBusy || _modelTransition || _downloadedModelRefreshRunning)
            return;
        _pendingDownloadedModelRefresh = false;
        _downloadedModelRefreshRunning = true;
        try { await RefreshModelsAsync(); }
        finally
        {
            _downloadedModelRefreshRunning = false;
            if (_pendingDownloadedModelRefresh && !_isBusy && !_modelTransition)
                Dispatcher.UIThread.Post(() => _ = RefreshDownloadedModelsAsync());
        }
    }

    private async Task StopModelDownloadsAsync()
    {
        _modelDownloadCancellation?.Cancel();
        Task? running = _modelDownloadTask;
        if (running is not null) await running;
    }
}
