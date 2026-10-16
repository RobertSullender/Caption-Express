using System.Diagnostics;
using System.Globalization;

namespace CaptionExpress.Media;

public sealed record ExportResult(string Path, double Duration, Bucket? Bucket, double CropX, double CropY,
    int? FrameCount = null, double? FrameRate = null, double? RequestedEnd = null,
    double? LastSourceFrameTime = null);

public sealed record VideoExportFormat(string Id, string Name, string Extension)
{
    public static readonly VideoExportFormat H264Mp4 = new("h264-mp4", "MP4 · H.264 + AAC", ".mp4");
    public static readonly VideoExportFormat HevcMp4 = new("hevc-mp4", "MP4 · H.265/HEVC + AAC", ".mp4");
    public static readonly VideoExportFormat Av1Mp4 = new("av1-mp4", "MP4 · AV1 + AAC", ".mp4");
    public static readonly VideoExportFormat WebM = new("vp9-webm", "WebM · VP9 + Opus", ".webm");
    public static readonly VideoExportFormat ProResMov = new("prores-mov", "MOV · ProRes 422 + PCM", ".mov");
    public static readonly IReadOnlyList<VideoExportFormat> All =
        [H264Mp4, HevcMp4, Av1Mp4, WebM, ProResMov];
    public override string ToString() => Name;
}

public sealed record TrainingExportPlan(int FrameCount, double FrameRate, double Duration, double SourceEnd)
{
    public static TrainingExportPlan ForRange(double start, double requestedEnd, double sourceDuration,
        TrainingExportPreset preset, double frameRate)
    {
        ArgumentNullException.ThrowIfNull(preset);
        preset.ValidateFrameRate(frameRate);
        if (preset.TemporalFactor <= 0 || preset.MinimumFrames < 2 ||
            (preset.MinimumFrames - 1) % preset.TemporalFactor != 0)
            throw new ArgumentException("The export preset has invalid temporal constraints.", nameof(preset));
        if (!double.IsFinite(start) || !double.IsFinite(requestedEnd) ||
            !double.IsFinite(sourceDuration) || start < 0 || requestedEnd <= start ||
            requestedEnd > sourceDuration + 0.05)
            throw new ArgumentException("Select a valid start and end within the media duration.");
        if (requestedEnd - start < preset.MinimumFrames / frameRate - 0.00001)
            throw new ArgumentException($"Select at least {preset.MinimumFrames / frameRate:0.###} seconds " +
                $"for a {preset.MinimumFrames}-frame {preset.Name} video.");
        int factor = preset.TemporalFactor;
        int minimumN = (preset.MinimumFrames - 1) / factor;
        // The VAE encodes one initial frame, then groups of TemporalFactor.
        // The selected end is an inclusive bound for the last sampled picture.
        // A video's presentation duration includes the final frame's display time.
        double selectedFrameEstimate = (requestedEnd - start) * frameRate;
        double availableFrameEstimate = (sourceDuration - start) * frameRate;
        if (selectedFrameEstimate > int.MaxValue - factor || availableFrameEstimate > int.MaxValue - factor)
            throw new ArgumentException("The selected video range is too long for a single export.");
        int desiredN = Math.Max(minimumN, (int)Math.Round((selectedFrameEstimate - 1) / factor,
            MidpointRounding.AwayFromZero));
        // The requested end may coincide with a frame timestamp if the source
        // continues. At the actual source tail, there is no frame at duration.
        // Allow millisecond-truncated container durations (5.041 for 121/24).
        int selectedFrames = (int)Math.Floor(selectedFrameEstimate + frameRate * 0.001 + 0.00001) + 1;
        int sourceFrames = (int)Math.Floor(availableFrameEstimate + frameRate * 0.001 + 0.00001);
        int maximumN = (Math.Min(selectedFrames, sourceFrames) - 1) / factor;
        if (maximumN < minimumN)
            throw new ArgumentException($"Select at least {preset.MinimumFrames / frameRate:0.###} " +
                $"seconds of source video for a {preset.MinimumFrames}-frame {preset.Name} clip.");
        int frames = factor * Math.Min(desiredN, maximumN) + 1;
        double duration = frames / frameRate;
        return new TrainingExportPlan(frames, frameRate, duration,
            Math.Min(start + (frames - 1) / frameRate, sourceDuration));
    }
}

// Compatibility for older callers that used the original fixed LTX export plan.
public sealed record LtxExportPlan(int FrameCount, double FrameRate, double Duration, double SourceEnd)
{
    public static LtxExportPlan ForRange(double start, double requestedEnd, double sourceDuration)
    {
        var plan = TrainingExportPlan.ForRange(start, requestedEnd, sourceDuration,
            TrainingExportPreset.Ltx23, 24);
        return new LtxExportPlan(plan.FrameCount, plan.FrameRate, plan.Duration, plan.SourceEnd);
    }
}

public static class MediaExporter
{
    public static async Task<ExportResult> ExportAsync(MediaInfo source, string directory, double startSeconds,
        double endSeconds, Bucket? bucket, double cropX, double cropY, CancellationToken cancellationToken = default,
        VideoExportFormat? videoFormat = null, TrainingExportPreset? preset = null, double? frameRate = null,
        bool includeAudio = true)
    {
        if (!double.IsFinite(startSeconds) || !double.IsFinite(endSeconds) ||
            !double.IsFinite(source.Duration) || startSeconds < 0 || endSeconds <= startSeconds ||
            endSeconds > source.Duration + 0.05)
            throw new ArgumentException("Select a valid start and end within the media duration.");
        if (!double.IsFinite(cropX) || !double.IsFinite(cropY))
            throw new ArgumentException("Choose a valid crop position.");
        if (source.HasVideo && bucket is null) throw new ArgumentException("Choose an output bucket.");
        preset ??= TrainingExportPreset.Ltx23;
        videoFormat ??= preset.DefaultVideoFormat;
        if (!VideoExportFormat.All.Contains(videoFormat))
            throw new ArgumentException("Choose a supported video output format.", nameof(videoFormat));
        if (source.HasVideo) preset.ValidateBucket(bucket!);
        double effectiveFrameRate = frameRate ?? preset.ChooseFrameRate(source.FrameRate);
        TrainingExportPlan? plan = source.HasVideo
            ? TrainingExportPlan.ForRange(startSeconds, endSeconds, source.Duration, preset, effectiveFrameRate) : null;
        Directory.CreateDirectory(directory);
        string extension = source.HasVideo ? videoFormat.Extension : ".flac";
        string partial = NumberedExportPaths.TemporaryPath(directory, extension);
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-n",
            "-ss", Number(startSeconds), "-i", source.Path,
            "-t", Number(plan?.Duration ?? (endSeconds - startSeconds))
        };
        if (source.HasVideo)
        {
            args.AddRange(["-map", "0:V:0"]);
            if (includeAudio) args.AddRange(["-map", "0:a:0?"]);
            else args.Add("-an");
            args.AddRange(["-vf",
                $"trim=end={Number(Math.Min(endSeconds, source.Duration) - startSeconds + 0.000001)}," +
                "setpts=PTS-STARTPTS," +
                "tpad=stop_mode=clone:stop=1," +
                VideoFilter(bucket!, cropX, cropY, plan!.FrameRate) +
                $",trim=end_frame={plan.FrameCount},setpts=PTS-STARTPTS"]);
            if (videoFormat == VideoExportFormat.H264Mp4)
                args.AddRange(["-pix_fmt", "yuv420p", "-c:v", "libx264", "-preset", "medium", "-crf", "18",
                    "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"]);
            else if (videoFormat == VideoExportFormat.HevcMp4)
                args.AddRange(["-pix_fmt", "yuv420p", "-c:v", "libx265", "-preset", "medium", "-crf", "20",
                    "-tag:v", "hvc1", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"]);
            else if (videoFormat == VideoExportFormat.Av1Mp4)
                args.AddRange(["-pix_fmt", "yuv420p", "-c:v", "libsvtav1", "-preset", "6", "-crf", "24",
                    "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"]);
            else if (videoFormat == VideoExportFormat.WebM)
                args.AddRange(["-pix_fmt", "yuv420p", "-c:v", "libvpx-vp9", "-deadline", "good", "-cpu-used", "3",
                    "-row-mt", "1", "-crf", "24", "-b:v", "0", "-c:a", "libopus", "-b:a", "192k"]);
            else if (videoFormat == VideoExportFormat.ProResMov)
                args.AddRange(["-pix_fmt", "yuv422p10le", "-c:v", "prores_ks", "-profile:v", "2",
                    "-c:a", "pcm_s24le"]);
            else throw new ArgumentException("Choose a supported video output format.", nameof(videoFormat));
            if (includeAudio && source.HasAudio)
                args.AddRange(["-af", $"atrim=end={Number(Math.Min(endSeconds, source.Duration) - startSeconds)}," +
                    "asetpts=PTS-STARTPTS,apad"]);
        }
        else args.AddRange(["-vn", "-map", "0:a:0", "-c:a", "flac"]);
        args.Add(partial);
        try
        {
            await RunAsync(args, cancellationToken);
            if (plan is not null) await VerifyVideoExportAsync(partial, bucket!, plan, cancellationToken);
            if (source.HasVideo)
            {
                var exportedMedia = await MediaProbe.ReadAsync(partial, cancellationToken);
                if (exportedMedia.HasAudio != (includeAudio && source.HasAudio))
                    throw new InvalidOperationException(includeAudio
                        ? "The exported video lost its source audio stream. Review the source audio before retrying."
                        : "The video-only export unexpectedly contains audio.");
            }
            string final = await NumberedExportPaths.PublishAsync(directory, partial, extension, cancellationToken);
            return new ExportResult(final, plan?.Duration ?? (endSeconds - startSeconds), bucket, cropX, cropY,
                plan?.FrameCount, plan?.FrameRate, endSeconds, plan?.SourceEnd);
        }
        catch
        {
            if (File.Exists(partial)) File.Delete(partial);
            throw;
        }
    }

    public static string VideoFilter(Bucket bucket, double cropX, double cropY)
        => VideoFilter(bucket, cropX, cropY, 24);

    public static string VideoFilter(Bucket bucket, double cropX, double cropY, double frameRate)
    {
        cropX = Math.Clamp(cropX, 0, 1);
        cropY = Math.Clamp(cropY, 0, 1);
        // FFmpeg evaluates offsets after autorotation and scaling; no source-size assumptions are needed.
        return $"scale=trunc(iw*sar/2)*2:ih,setsar=1,fps={Number(frameRate)}:start_time=0:round=near," +
               $"scale={bucket.Width}:{bucket.Height}:force_original_aspect_ratio=increase," +
               $"crop={bucket.Width}:{bucket.Height}:(in_w-out_w)*{Number(cropX)}:(in_h-out_h)*{Number(cropY)},setsar=1";
    }

    private static async Task VerifyVideoExportAsync(string path, Bucket bucket, TrainingExportPlan plan,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("ffprobe")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        foreach (string argument in new[] { "-v", "error", "-select_streams", "v:0", "-count_frames",
                     "-show_entries", "stream=width,height,avg_frame_rate,nb_read_frames",
                     "-of", "json", path }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start ffprobe.");
        using var cancellation = cancellationToken.Register(() =>
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        });
        string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        string error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) throw new InvalidOperationException("Could not verify export: " + error);
        using var document = System.Text.Json.JsonDocument.Parse(output);
        var stream = document.RootElement.GetProperty("streams")[0];
        string[] rateParts = (stream.GetProperty("avg_frame_rate").GetString() ?? "").Split('/');
        bool validRate = rateParts.Length == 2 &&
            double.TryParse(rateParts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double numerator) &&
            double.TryParse(rateParts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double denominator) &&
            denominator > 0 && Math.Abs(numerator / denominator - plan.FrameRate) < 0.0001;
        if (stream.GetProperty("width").GetInt32() != bucket.Width ||
            stream.GetProperty("height").GetInt32() != bucket.Height ||
            stream.GetProperty("nb_read_frames").GetString() != plan.FrameCount.ToString(CultureInfo.InvariantCulture) ||
            !validRate)
            throw new InvalidOperationException($"Export was not exactly {bucket.Width}×{bucket.Height}, " +
                $"{Number(plan.FrameRate)} fps, and {plan.FrameCount} frames. Check the selected source range.");
    }

    public static async Task<byte[]> FrameAsync(string path, double seconds, CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-ss", Number(seconds),
                     "-i", path, "-map", "0:V:0", "-frames:v", "1", "-vf",
                     "scale=trunc(iw*sar/2)*2:ih,setsar=1,scale=960:960:force_original_aspect_ratio=decrease",
                     "-f", "image2pipe", "-vcodec", "png", "pipe:1" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        using var cancellation = cancellationToken.Register(() =>
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        });
        using var data = new MemoryStream();
        var output = process.StandardOutput.BaseStream.CopyToAsync(data, cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await Task.WhenAll(output, process.WaitForExitAsync(cancellationToken));
        if (process.ExitCode != 0 || data.Length == 0)
            throw new InvalidOperationException($"Could not read a video frame: {await error}");
        return data.ToArray();
    }

    public static async Task<byte[]> WaveformAsync(string path, double centerSeconds, double duration,
        CancellationToken cancellationToken = default)
    {
        double startSecond = Math.Clamp(centerSeconds - 30, 0, Math.Max(0, duration - 60));
        double length = Math.Min(60, duration - startSecond);
        var start = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin",
                     "-ss", Number(startSecond), "-t", Number(length), "-i", path,
                     "-filter_complex", "aformat=channel_layouts=mono,showwavespic=s=960x240:colors=0x60d6e8",
                     "-frames:v", "1", "-f", "image2pipe", "-vcodec", "png", "pipe:1" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        using var cancellation = cancellationToken.Register(() =>
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        });
        using var data = new MemoryStream();
        var output = process.StandardOutput.BaseStream.CopyToAsync(data, cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await Task.WhenAll(output, process.WaitForExitAsync(cancellationToken));
        if (process.ExitCode != 0 || data.Length == 0)
            throw new InvalidOperationException($"Could not draw audio waveform: {await error}");
        return data.ToArray();
    }

    private static async Task RunAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        using var cancellation = cancellationToken.Register(() =>
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        });
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) throw new InvalidOperationException($"FFmpeg export failed: {await error}");
    }

    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
