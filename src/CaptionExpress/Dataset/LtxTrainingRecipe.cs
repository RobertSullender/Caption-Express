using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CaptionExpress.Dataset;

/// <summary>
/// Builds an LTX 2.3 preprocessing proposal from the files actually published.
/// Counting decoded frames prevents a trainer bucket from silently discarding
/// the end of a reviewed caption's clip.
/// </summary>
public static class LtxTrainingRecipe
{
    public sealed record VideoShape(int Width, int Height, int Frames, double FrameRate, bool HasAudio)
    {
        public string Bucket => $"{Width}x{Height}x{Frames}";
    }

    public static async Task<VideoShape> InspectAsync(string path, CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo("ffprobe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string argument in new[]
                 {
                     "-v", "error", "-count_frames",
                     "-show_entries", "stream=codec_type,width,height,nb_read_frames,avg_frame_rate,sample_aspect_ratio",
                     "-of", "json", path
                 })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start ffprobe.");
        using var cancellation = cancellationToken.Register(() =>
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        });
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
            throw new IOException($"Could not decode {Path.GetFileName(path)}: {await error}");

        using var document = JsonDocument.Parse(await output);
        if (!document.RootElement.TryGetProperty("streams", out var streams) ||
            streams.GetArrayLength() == 0)
            throw new IOException($"The video has no decodable picture stream: {Path.GetFileName(path)}");
        JsonElement stream = default;
        bool hasAudio = false;
        foreach (JsonElement candidate in streams.EnumerateArray())
        {
            string? kind = candidate.TryGetProperty("codec_type", out var codecType)
                ? codecType.GetString() : null;
            if (kind == "video" && stream.ValueKind == JsonValueKind.Undefined) stream = candidate;
            if (kind == "audio") hasAudio = true;
        }
        if (stream.ValueKind == JsonValueKind.Undefined)
            throw new IOException($"The video has no decodable picture stream: {Path.GetFileName(path)}");
        int width = stream.GetProperty("width").GetInt32();
        int height = stream.GetProperty("height").GetInt32();
        string frameText = stream.TryGetProperty("nb_read_frames", out var frameProperty)
            ? frameProperty.GetString() ?? "" : "";
        if (!int.TryParse(frameText, NumberStyles.None, CultureInfo.InvariantCulture, out int frames) ||
            frames < 9 || frames % 8 != 1)
            throw new IOException($"{Path.GetFileName(path)} has {frameText} decoded frames; " +
                                  "LTX 2.3 video exports need 8n+1 frames (at least 9).");
        if (width <= 0 || height <= 0 || width % 32 != 0 || height % 32 != 0)
            throw new IOException($"{Path.GetFileName(path)} is {width}x{height}; " +
                                  "LTX 2.3 export dimensions must be multiples of 32.");
        if (stream.TryGetProperty("sample_aspect_ratio", out var aspect) &&
            aspect.GetString() is { } ratio && ratio != "1:1" && ratio != "0:1" && ratio != "N/A")
            throw new IOException($"{Path.GetFileName(path)} has non-square pixels ({ratio}).");
        string rateText = stream.TryGetProperty("avg_frame_rate", out var rate)
            ? rate.GetString() ?? "" : "";
        double frameRate = ParseRate(rateText);
        if (!double.IsFinite(frameRate) || frameRate <= 0)
            throw new IOException($"Could not read the frame rate of {Path.GetFileName(path)}.");
        return new VideoShape(width, height, frames, frameRate, hasAudio);
    }

    public static string Render(IReadOnlyList<VideoShape> shapes,
        ProjectMediaPolicy mediaPolicy = ProjectMediaPolicy.LegacyMixed)
    {
        if (shapes.Count == 0) throw new ArgumentException("The dataset has no videos.", nameof(shapes));
        if (shapes.Any(shape => shape.Width <= 0 || shape.Height <= 0 ||
                                shape.Frames < 9 || shape.Frames % 8 != 1 ||
                                !double.IsFinite(shape.FrameRate) || shape.FrameRate <= 0))
            throw new ArgumentException("The training recipe has an invalid video shape.", nameof(shapes));
        string[] buckets = shapes.Select(shape => shape.Bucket).Distinct(StringComparer.Ordinal)
            .OrderBy(bucket => bucket, StringComparer.Ordinal).ToArray();
        string rates = string.Join(", ", shapes.Select(shape =>
                shape.FrameRate.ToString("0.###", CultureInfo.InvariantCulture))
            .Distinct(StringComparer.Ordinal).OrderBy(rate => rate, StringComparer.Ordinal));
        var sharedRatioAndFrames = shapes.GroupBy(shape =>
            (Width: shape.Width / GreatestCommonDivisor(shape.Width, shape.Height),
                Height: shape.Height / GreatestCommonDivisor(shape.Width, shape.Height), shape.Frames))
            .Where(group => group.Select(shape => (shape.Width, shape.Height)).Distinct().Count() > 1)
            .ToArray();
        var script = new StringBuilder();
        script.AppendLine("#!/usr/bin/env bash");
        script.AppendLine("set -euo pipefail");
        script.AppendLine("# Caption-Express LTX 2.3 dataset preprocessing proposal.");
        script.AppendLine($"# Reviewed videos: {shapes.Count}; frame rates: {rates} fps.");
        int withAudio = shapes.Count(shape => shape.HasAudio);
        int withoutAudio = shapes.Count - withAudio;
        if (mediaPolicy == ProjectMediaPolicy.JointVideoAudio && withoutAudio > 0)
            throw new ArgumentException("Joint video + audio training requires an audio stream in every finalized video.",
                nameof(shapes));
        if (mediaPolicy == ProjectMediaPolicy.VideoOnly && withAudio > 0)
            throw new ArgumentException("Video-only project exports must have no audio stream.", nameof(shapes));
        script.AppendLine($"# Embedded audio streams: {withAudio} with audio; {withoutAudio} without audio.");
        if (mediaPolicy == ProjectMediaPolicy.JointVideoAudio)
            script.AppendLine("# Project type: joint video + audio. Every published video has an embedded audio stream.");
        if (mediaPolicy == ProjectMediaPolicy.VideoOnly)
        {
            script.AppendLine("# Project type: video only. Exports contain no audio stream.");
            script.AppendLine("# Remove training_strategy.audio from the trainer config before training.");
            script.AppendLine("# Set validation.generate_audio: false to avoid unused audio validation output.");
            script.AppendLine("# --skip-audio below avoids generating unused audio latents during preprocessing.");
        }
        if (mediaPolicy == ProjectMediaPolicy.LegacyMixed && withoutAudio > 0)
        {
            script.AppendLine("# WARNING: Videos without audio get video latents and text embeddings but no audio latents during preprocessing.");
            script.AppendLine("# Stock configs/t2v_lora.yaml requires audio latents for every sample;");
            script.AppendLine("# its joint audio+video training excludes videos without audio (see skipped-sample log).");
            script.AppendLine("# For joint training, review/exclude these clips or deliberately add an appropriate audio track.");
            script.AppendLine("# To train all clips as video-only, omit training_strategy.audio from the training config.");
            script.AppendLine("# Verify audio_latents after preprocessing; an audio decode failure can have the same effect.");
        }
        script.AppendLine($"# Exact exported width x height x frames: {string.Join(";", buckets)}");
        script.AppendLine("# The frame counts match the published clips, avoiding temporal truncation.");
        foreach (var group in sharedRatioAndFrames)
            script.AppendLine($"# Spatial warning: same-ratio {group.Key.Frames}-frame clips " +
                              $"({string.Join(", ", group.Select(shape => shape.Bucket).Distinct())}) " +
                              "share a trainer bucket choice; LTX selects the largest area and resizes smaller clips.");
        if (buckets.Length > 1)
            script.AppendLine("# Set optimization.batch_size: 1 in the trainer config for mixed buckets.");
        script.AppendLine("# Use the LTX 2.3 unified checkpoint and its matching Gemma 3 text encoder.");
        script.AppendLine("# After preprocessing, adapt configs/t2v_lora.yaml for your training host.");
        script.AppendLine("# Set data.preprocessed_data_root to this dataset's .precomputed directory,");
        script.AppendLine("# then run: uv run python scripts/train.py /path/to/your-config.yaml");
        script.AppendLine("# Run: LTX_TRAINER_ROOT=/path/to/ltx-trainer LTX_MODEL_PATH=/path/to/ltx-2.3.safetensors \\");
        script.AppendLine("#      LTX_TEXT_ENCODER_PATH=/path/to/gemma-root bash training-recipe.sh");
        script.AppendLine("# Extra process_dataset.py flags may be appended, for example --overwrite.");
        script.AppendLine(": \"${LTX_TRAINER_ROOT:?Set LTX_TRAINER_ROOT to the ltx-trainer directory}\"");
        script.AppendLine(": \"${LTX_MODEL_PATH:?Set LTX_MODEL_PATH to the LTX 2.3 unified checkpoint}\"");
        script.AppendLine(": \"${LTX_TEXT_ENCODER_PATH:?Set LTX_TEXT_ENCODER_PATH to the matching Gemma 3 root}\"");
        script.AppendLine("dataset_dir=\"$(cd -- \"$(dirname -- \"${BASH_SOURCE[0]}\")\" && pwd)\"");
        script.AppendLine("cd -- \"$LTX_TRAINER_ROOT\"");
        script.AppendLine("uv run python scripts/process_dataset.py \"$dataset_dir/dataset.jsonl\" \\");
        script.AppendLine($"    --resolution-buckets \"{string.Join(";", buckets)}\" \\");
        script.AppendLine("    --batch-size 1 \\");
        if (mediaPolicy == ProjectMediaPolicy.VideoOnly)
            script.AppendLine("    --skip-audio \\");
        script.AppendLine("    --model-path \"$LTX_MODEL_PATH\" \\");
        script.AppendLine("    --text-encoder-path \"$LTX_TEXT_ENCODER_PATH\" \"$@\"");
        return script.ToString();
    }

    private static double ParseRate(string value)
    {
        string[] parts = value.Split('/');
        if (parts.Length is < 1 or > 2 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double numerator))
            return double.NaN;
        double denominator = 1;
        if (parts.Length == 2 &&
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out denominator))
            return double.NaN;
        return denominator == 0 ? double.NaN : numerator / denominator;
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0) (left, right) = (right, left % right);
        return left;
    }
}
