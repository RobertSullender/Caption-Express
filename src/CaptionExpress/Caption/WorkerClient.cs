using System.Diagnostics;
using System.Text.Json;

namespace CaptionExpress.Caption;

public sealed class WorkerClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly Queue<string> _errors = new();

    public bool ModelPresent { get; private set; }
    public IReadOnlyList<WorkerModel> Models { get; private set; } = [];
    public string? DefaultModelId { get; private set; }
    public string DefaultUserPrompt { get; private set; } = "";
    public string? LoadedModelId { get; private set; }
    public string? LoadedModelLabel { get; private set; }
    public bool? LoadedModelThinking { get; private set; }
    public bool IsAlive => !_process.HasExited;

    private WorkerClient(Process process)
    {
        _process = process;
        _process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (string.IsNullOrWhiteSpace(eventArgs.Data)) return;
            lock (_errors)
            {
                _errors.Enqueue(eventArgs.Data);
                while (_errors.Count > 30) _errors.Dequeue();
            }
        };
        _process.BeginErrorReadLine();
    }

    public static async Task<WorkerClient> StartAsync(string pythonPath, string scriptPath,
        CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo(pythonPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(scriptPath)!
        };
        start.ArgumentList.Add(scriptPath);
        start.Environment["PYTHONUNBUFFERED"] = "1";
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the Python worker.");
        var client = new WorkerClient(process);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var ready = await client.ReadAsync(timeout.Token);
            if (ready.GetProperty("type").GetString() != "ready")
                throw new InvalidOperationException("The caption worker did not start correctly.");
            client.UpdateModels(ready);
            client.UpdateLoadedModel(ready);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    public async Task<JsonElement> RequestAsync(object request, Func<JsonElement, Task>? onEvent = null,
        CancellationToken cancellationToken = default)
    {
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            var json = JsonSerializer.Serialize(request);
            await _process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken);
            await _process.StandardInput.FlushAsync(cancellationToken);
            while (true)
            {
                var response = await ReadAsync(cancellationToken);
                string type = response.GetProperty("type").GetString() ?? "";
                if (type == "error")
                    throw new InvalidOperationException(response.GetProperty("message").GetString() ?? "Caption worker failed.");
                if (onEvent is not null) await onEvent(response);
                if (type == "status") UpdateModels(response);
                if (type is "status" or "complete") UpdateLoadedModel(response);
                if (type is "complete" or "status") return response;
            }
        }
        catch (OperationCanceledException)
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            throw;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task<JsonElement> ReadAsync(CancellationToken cancellationToken)
    {
        string? line = await _process.StandardOutput.ReadLineAsync(cancellationToken);
        if (line is null)
        {
            string tail;
            lock (_errors) tail = string.Join(Environment.NewLine, _errors);
            throw new InvalidOperationException("Python worker exited unexpectedly. " + tail);
        }
        try { return JsonDocument.Parse(line).RootElement.Clone(); }
        catch (JsonException error) { throw new InvalidOperationException("Worker sent invalid JSON: " + line, error); }
    }

    private void UpdateModels(JsonElement response)
    {
        if (response.TryGetProperty("model_present", out var present) &&
            present.ValueKind is JsonValueKind.True or JsonValueKind.False)
            ModelPresent = present.GetBoolean();
        if (response.TryGetProperty("default_model_id", out var defaultModel))
            DefaultModelId = defaultModel.ValueKind == JsonValueKind.String ? defaultModel.GetString() : null;
        if (response.TryGetProperty("default_user_prompt", out var defaultPrompt) &&
            defaultPrompt.ValueKind == JsonValueKind.String)
            DefaultUserPrompt = defaultPrompt.GetString() ?? "";
        if (response.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
            Models = models.EnumerateArray()
                .Where(model => model.ValueKind == JsonValueKind.Object)
                .Select(model => new WorkerModel(ReadString(model, "id"), ReadString(model, "label"),
                    ReadString(model, "family")))
                .Where(model => !string.IsNullOrWhiteSpace(model.Id))
                .ToArray();

    }

    private void UpdateLoadedModel(JsonElement response)
    {
        if (response.TryGetProperty("loaded_model_id", out var id))
            LoadedModelId = id.ValueKind == JsonValueKind.String ? id.GetString() : null;
        if (response.TryGetProperty("loaded_model_label", out var label))
            LoadedModelLabel = label.ValueKind == JsonValueKind.String ? label.GetString() : null;
        if (response.TryGetProperty("loaded_model_thinking", out var thinking))
            LoadedModelThinking = thinking.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? thinking.GetBoolean() : null;
    }

    private static string ReadString(JsonElement item, string key) =>
        item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                try { await _process.StandardInput.WriteLineAsync("{\"command\":\"shutdown\"}"); }
                catch (Exception error) when (error is IOException or InvalidOperationException or ObjectDisposedException) { }
                await _process.WaitForExitAsync(TimeSpan.FromSeconds(12));
            }
        }
        finally
        {
            if (!_process.HasExited)
            {
                try { _process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { } // The worker exited between the check and Kill.
                await _process.WaitForExitAsync(TimeSpan.FromSeconds(5));
            }
            _process.Dispose();
            _requestGate.Dispose();
        }
    }
}

public sealed record WorkerModel(string Id, string Label, string Family)
{
    public override string ToString() => Label;
}

internal static class ProcessWait
{
    public static async Task<bool> WaitForExitAsync(this Process process, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try { await process.WaitForExitAsync(cancellation.Token); return true; }
        catch (OperationCanceledException) { return false; }
    }
}
