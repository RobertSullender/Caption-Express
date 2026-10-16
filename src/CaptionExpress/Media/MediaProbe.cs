using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace CaptionExpress.Media;

public sealed record MediaInfo(string Path, double Duration, int Width, int Height, bool HasVideo, bool HasAudio,
    double? FrameRate = null, string? VideoCodec = null, string? AudioCodec = null)
{
    public string DisplaySize => HasVideo ? $"{Width} × {Height}" : "Audio only";
}

public static class MediaProbe
{
    public static async Task<MediaInfo> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Media file not found.", path);
        var start = new ProcessStartInfo("ffprobe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[] { "-v", "error", "-show_format", "-show_streams", "-of", "json", path })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start ffprobe.");
        using var cancellation = cancellationToken.Register(() =>
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        });
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) throw new InvalidOperationException($"ffprobe failed: {await error}");

        using var document = JsonDocument.Parse(await output);
        var root = document.RootElement;
        JsonElement video = default;
        JsonElement audio = default;
        foreach (var stream in root.GetProperty("streams").EnumerateArray())
        {
            var kind = stream.GetProperty("codec_type").GetString();
            if (kind == "video" && video.ValueKind == JsonValueKind.Undefined &&
                (!stream.TryGetProperty("disposition", out var disposition) ||
                 !disposition.TryGetProperty("attached_pic", out var attached) || attached.GetInt32() == 0)) video = stream;
            if (kind == "audio" && audio.ValueKind == JsonValueKind.Undefined) audio = stream;
        }
        bool hasVideo = video.ValueKind != JsonValueKind.Undefined;
        bool hasAudio = audio.ValueKind != JsonValueKind.Undefined;
        if (!hasVideo && !hasAudio) throw new InvalidOperationException("No video or audio stream was found.");

        double duration = ReadDuration(root.GetProperty("format"));
        if (duration <= 0 && hasVideo) duration = ReadDuration(video);
        if (duration <= 0 && hasAudio) duration = ReadDuration(audio);
        // Some containers run slightly longer because of the audio stream. The trim
        // range must end where picture exists, otherwise an 8n+1 export needs filler.
        double videoDuration = hasVideo ? ReadDuration(video) : 0;
        if (videoDuration > 0) duration = Math.Min(duration, videoDuration);
        if (duration <= 0) throw new InvalidOperationException("The file has no readable duration.");

        int width = hasVideo ? video.GetProperty("width").GetInt32() : 0;
        int height = hasVideo ? video.GetProperty("height").GetInt32() : 0;
        if (hasVideo)
        {
            if (width <= 0 || height <= 0)
                throw new InvalidOperationException("The video has invalid dimensions.");
            // Bucket choice and crop preview use the displayed square-pixel shape.
            // Anamorphic sources store a different coded width and pixel aspect ratio.
            double displayWidth = width * ReadSampleAspectRatio(video);
            double displayHeight = height;
            if (ReadRotation(video) % 180 != 0)
                (displayWidth, displayHeight) = (displayHeight, displayWidth);
            width = checked((int)Math.Round(displayWidth));
            height = checked((int)Math.Round(displayHeight));
        }
        return new MediaInfo(path, duration, width, height, hasVideo, hasAudio,
            hasVideo ? ReadFrameRate(video) : null,
            hasVideo && video.TryGetProperty("codec_name", out var videoCodec) ? videoCodec.GetString() : null,
            hasAudio && audio.TryGetProperty("codec_name", out var audioCodec) ? audioCodec.GetString() : null);
    }

    private static double ReadDuration(JsonElement element)
    {
        if (!element.TryGetProperty("duration", out var duration)) return 0;
        return double.TryParse(duration.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && double.IsFinite(value) && value > 0 ? value : 0;
    }

    private static double? ReadFrameRate(JsonElement video)
    {
        foreach (string name in new[] { "avg_frame_rate", "r_frame_rate" })
        {
            if (!video.TryGetProperty(name, out var rate) || rate.ValueKind != JsonValueKind.String)
                continue;
            string[] parts = (rate.GetString() ?? "").Split('/');
            if (parts.Length is < 1 or > 2 ||
                !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double numerator))
                continue;
            double denominator = 1;
            if (parts.Length == 2 &&
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out denominator))
                continue;
            double fps = numerator / denominator;
            if (double.IsFinite(fps) && fps > 0) return fps;
        }
        return null;
    }

    private static int ReadRotation(JsonElement video)
    {
        if (video.TryGetProperty("tags", out var tags) && tags.TryGetProperty("rotate", out var tag) &&
            int.TryParse(tag.GetString(), out var tagged)) return ((tagged % 360) + 360) % 360;
        if (video.TryGetProperty("side_data_list", out var sideData))
            foreach (var item in sideData.EnumerateArray())
                if (item.TryGetProperty("rotation", out var rotation))
                    return ((rotation.GetInt32() % 360) + 360) % 360;
        return 0;
    }

    private static double ReadSampleAspectRatio(JsonElement video)
    {
        if (!video.TryGetProperty("sample_aspect_ratio", out var ratio) ||
            ratio.GetString() is not { } text) return 1;
        var parts = text.Split(':');
        if (parts.Length != 2 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) ||
            denominator <= 0) return 1;
        double value = numerator / denominator;
        return double.IsFinite(value) && value > 0 ? value : 1;
    }
}
