using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CaptionExpress.Caption;
using CaptionExpress.Dataset;
using CaptionExpress.Media;

string temp = Path.Combine(Path.GetTempPath(), "caption-express-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    Require(Timecode.TryParse("01:02:03.456", out double parsedTime) &&
            Math.Abs(parsedTime - 3723.456) < 0.00001, "ordinary timecode parses");
    Require(!Timecode.TryParse("00:00:NaN", out _) &&
            !Timecode.TryParse("00:00:Infinity", out _), "nonfinite timecodes are rejected");
    try
    {
        LtxExportPlan.ForRange(double.NaN, 1, 2);
        throw new Exception("Nonfinite export range unexpectedly accepted.");
    }
    catch (ArgumentException) { }
    string source = Path.Combine(temp, "original.mp4");
    await Run("ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
        "-f", "lavfi", "-i", "color=c=red:s=640x360:r=24",
        "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=16000",
        "-t", "4", "-vf", "drawbox=x=320:y=0:w=320:h=360:color=blue:t=fill",
        "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", source);
    byte[] originalHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
    var info = await MediaProbe.ReadAsync(source);
    Require(info.HasVideo && info.HasAudio && info.Width == 640 && info.Height == 360 &&
            info.FrameRate == 24, "source probe includes frame rate");
    Require(ProjectMediaPolicyRules.GetRejection(ProjectMediaPolicy.JointVideoAudio,
                info.HasVideo, info.HasAudio) is null &&
            ProjectMediaPolicyRules.GetRejection(ProjectMediaPolicy.VideoOnly,
                info.HasVideo, info.HasAudio) is null,
        "both new project types accept a source with video and embedded audio");
    Require(TrainingExportPreset.All.Count == 1 &&
            TrainingExportPreset.FindById("ltx-23") == TrainingExportPreset.Ltx23 &&
            TrainingExportPreset.FindById("unknown") is null,
        "LTX 2.3 is the only training preset");
    Require(TrainingExportPreset.Ltx23.Buckets.Select(bucket => (bucket.Name, bucket.Width, bucket.Height))
            .SequenceEqual(new[]
            {
                ("12:5", 1536, 640), ("7:4", 1344, 768), ("20:13", 1280, 832),
                ("9:7", 1152, 896), ("1:1", 1024, 1024), ("7:9", 896, 1152),
                ("13:20", 832, 1280), ("4:7", 768, 1344), ("5:12", 640, 1536)
            }) && TrainingExportPreset.Ltx23.Buckets.All(bucket =>
                bucket.Width % 32 == 0 && bucket.Height % 32 == 0),
        "the nine original buckets are LTX aligned and unchanged");
    var restoredPreset = JsonSerializer.Deserialize<TrainingExportPreset>(
        JsonSerializer.Serialize(TrainingExportPreset.Ltx23));
    Require(restoredPreset?.Id == "ltx-23" && restoredPreset.TemporalFactor == 8 &&
            restoredPreset.FrameRates.SequenceEqual([24.0]) &&
            restoredPreset.Buckets.Count == TrainingExportPreset.Ltx23.Buckets.Count &&
            restoredPreset.DefaultVideoFormat.Id == VideoExportFormat.H264Mp4.Id,
        "full export preset survives a settings snapshot round-trip");
    Require(TrainingExportPreset.Ltx23.ChooseFrameRate(24.1) == 24 &&
            TrainingExportPreset.Ltx23.ChooseFrameRate(24.9) == 24 &&
            TrainingExportPreset.Ltx23.ChooseFrameRate(null) == 24,
        "automatic LTX output uses 24 fps regardless of source rate");
    var customLtxBucket = new Bucket("Custom", 640, 384);
    TrainingExportPreset.Ltx23.ValidateBucket(customLtxBucket);
    Require(!TrainingExportPreset.Ltx23.Buckets.Contains(customLtxBucket),
        "valid custom bucket is accepted without being listed as a built-in bucket");
    try
    {
        TrainingExportPreset.Ltx23.ValidateBucket(new Bucket("Invalid", 650, 384));
        throw new Exception("Nonaligned LTX bucket unexpectedly accepted.");
    }
    catch (ArgumentException) { }
    Require(TrainingExportPreset.Ltx23.NearestBucket(1920, 1080).Width >
            TrainingExportPreset.Ltx23.NearestBucket(1920, 1080).Height,
        "automatic bucket follows source orientation");
    Require(TrainingExportPreset.Ltx23.NearestBucket(1024, 1024).Width == 1024 &&
            TrainingExportPreset.Ltx23.NearestBucket(512, 512).Width == 1024,
        "the sole square bucket is selected for square sources");
    var planned = LtxExportPlan.ForRange(1, 3, info.Duration);
    Require(planned.FrameCount == 49 && Math.Abs(planned.Duration - 49.0 / 24) < 0.00001,
        "nearest LTX frame plan");
    Require(LtxExportPlan.ForRange(0, info.Duration, info.Duration).FrameCount == 89,
        "source end prevents extension");
    var roundedTail = TrainingExportPlan.ForRange(0, 5.041, 5.041,
        TrainingExportPreset.Ltx23, 24);
    Require(roundedTail.FrameCount == 121 && roundedTail.SourceEnd <= 5.041,
        "millisecond-truncated duration retains all 121 available frames without claiming a later source end");
    var boundedTrim = TrainingExportPlan.ForRange(0, 0.9, info.Duration,
        TrainingExportPreset.Ltx23, 24);
    Require(boundedTrim.FrameCount == 17 && boundedTrim.SourceEnd <= 0.9,
        "frame snapping never samples a picture after the selected end");
    Require(Buckets.Nearest(640, 360).Name == "7:4", "nearest aspect bucket");
    var square = Buckets.All.Single(b => b.Name == "1:1");
    var left = await MediaExporter.ExportAsync(info, temp, 1, 3, square, 0, 0.5);
    var right = await MediaExporter.ExportAsync(info, temp, 1, 3, square, 1, 0.5);
    Require(Path.GetExtension(left.Path) == ".mp4" &&
            await StreamCodecs(left.Path) == ("h264", "aac"),
        "default MP4 uses H.264 and AAC");
    await DecodeVideoAndAudio(left.Path);
    string videoOnlyDirectory = Path.Combine(temp, "video-only-export");
    var videoOnly = await MediaExporter.ExportAsync(info, videoOnlyDirectory, 1, 3,
        new Bucket("small square", 128, 128), 0.5, 0.5, includeAudio: false);
    var videoOnlyInfo = await MediaProbe.ReadAsync(videoOnly.Path);
    Require(videoOnlyInfo.HasVideo && !videoOnlyInfo.HasAudio && videoOnly.FrameCount == 49 &&
            videoOnly.FrameRate == 24 && Path.GetFileName(videoOnly.Path) == "Vid-0001.mp4",
        "video-only export strips source audio while preserving the training frame window");
    var webm = await MediaExporter.ExportAsync(info, temp, 1, 3, square, 0.5, 0.5,
        videoFormat: VideoExportFormat.WebM);
    Require(Path.GetFileName(left.Path) == "Vid-0001.mp4" &&
            Path.GetFileName(right.Path) == "Vid-0002.mp4" &&
            Path.GetFileName(webm.Path) == "Vid-0003.webm",
        "all output formats share one numbered sequence");
    Require(Path.GetExtension(webm.Path) == ".webm" &&
            await StreamCodecs(webm.Path) == ("vp9", "opus") &&
            webm.FrameCount == 49 && webm.FrameCount % 8 == 1,
        "selectable WebM uses VP9 and Opus with the expected frame count");
    await DecodeVideoAndAudio(webm.Path);
    var shortBucket = new Bucket("codec smoke", 128, 128);
    var additionalFormats = new[]
    {
        (Format: VideoExportFormat.HevcMp4, Extension: ".mp4", Video: "hevc", Audio: "aac"),
        (Format: VideoExportFormat.Av1Mp4, Extension: ".mp4", Video: "av1", Audio: "aac"),
        (Format: VideoExportFormat.ProResMov, Extension: ".mov", Video: "prores", Audio: "pcm_s24le")
    };
    for (int index = 0; index < additionalFormats.Length; index++)
    {
        var option = additionalFormats[index];
        var exported = await MediaExporter.ExportAsync(info, temp, 0, 2, shortBucket, 0.5, 0.5,
            videoFormat: option.Format);
        Require(Path.GetFileName(exported.Path) == $"Vid-{index + 4:D4}{option.Extension}" &&
                await StreamCodecs(exported.Path) == (option.Video, option.Audio) &&
                exported.FrameCount == 49 && exported.FrameRate == 24,
            $"{option.Format.Name} uses the expected codecs, numbered path, and 8n+1 frame count");
        await DecodeVideoAndAudio(exported.Path);
        var (encodedVideoDuration, encodedAudioDuration) = await StreamDurations(exported.Path);
        Require(Math.Abs(encodedVideoDuration - encodedAudioDuration) < 0.05,
            $"{option.Format.Name} keeps audio and video aligned");
        if (option.Format == VideoExportFormat.ProResMov)
            Require(await VideoPixelFormat(exported.Path) == "yuv422p10le",
                "ProRes uses the 10-bit 4:2:2 pixel format");
    }
    string roundedSource = Path.Combine(temp, "rounded-121-frames.mp4");
    await Run("ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
        "-f", "lavfi", "-i", "color=c=green:s=128x72:r=24",
        "-frames:v", "121", "-c:v", "libx264", "-pix_fmt", "yuv420p", roundedSource);
    var roundedInfo = (await MediaProbe.ReadAsync(roundedSource)) with { Duration = 5.041 };
    var roundedExport = await MediaExporter.ExportAsync(roundedInfo, temp, 0, roundedInfo.Duration,
        new Bucket("Square small", 512, 512), 0.5, 0.5,
        preset: TrainingExportPreset.Ltx23, frameRate: 24);
    Require(roundedExport.FrameCount == 121 &&
            Math.Abs((await MediaProbe.ReadAsync(roundedExport.Path)).Duration - 121.0 / 24) < 0.01,
        "real 121-frame export survives a source duration rounded to milliseconds");
    var customFps25 = await MediaExporter.ExportAsync(info, temp, 0, 1, customLtxBucket,
        0.5, 0.5, preset: TrainingExportPreset.Ltx23, frameRate: 25);
    var customFps25Info = await MediaProbe.ReadAsync(customFps25.Path);
    Require(customFps25.FrameCount == 25 && customFps25.FrameRate == 25 &&
            customFps25Info.FrameRate == 25 &&
            customFps25Info.Width == customLtxBucket.Width &&
            customFps25Info.Height == customLtxBucket.Height,
        "explicit custom 25 fps and aligned bucket retain LTX 8n+1 frames");
    var ltx30 = await MediaExporter.ExportAsync(info, temp, 0, 1, shortBucket,
        0.5, 0.5, preset: TrainingExportPreset.Ltx23, frameRate: 30);
    Require(ltx30.FrameCount == 25 && ltx30.FrameRate == 30 &&
            (await MediaProbe.ReadAsync(ltx30.Path)).FrameRate == 30,
        "LTX accepts explicit custom 30 fps while keeping the last frame inside the trim");
    var leftInfo = await MediaProbe.ReadAsync(left.Path);
    var rightInfo = await MediaProbe.ReadAsync(right.Path);
    Require(leftInfo.Width == 1024 && leftInfo.Height == 1024 && leftInfo.HasAudio &&
            leftInfo.VideoCodec == "h264" && leftInfo.AudioCodec == "aac",
        "review metadata reports export dimensions and codecs");
    Require(rightInfo.Width == 1024 && rightInfo.Height == 1024 && rightInfo.HasAudio, "right export dimensions and audio");
    Require(left.FrameCount == 49 && left.FrameRate == 24 && left.FrameCount % 8 == 1,
        "24 fps and 8n+1 frame count");
    Require(Math.Abs(leftInfo.Duration - 49.0 / 24) < 0.02, "snapped LTX duration");
    var (videoDuration, audioDuration) = await StreamDurations(left.Path);
    Require(Math.Abs(videoDuration - audioDuration) < 0.05, "exported audio/video alignment");
    Require(Math.Abs(leftInfo.Duration - 2) < 0.15, "trim duration");
    var leftPixel = await CenterPixel(left.Path);
    var rightPixel = await CenterPixel(right.Path);
    Require(leftPixel[0] > leftPixel[2], "left crop shows red half");
    Require(rightPixel[2] > rightPixel[0], "right crop shows blue half");
    Require(SHA256.HashData(await File.ReadAllBytesAsync(source)).SequenceEqual(originalHash), "original untouched");
    foreach (var bucket in Buckets.All)
    {
        var sample = await MediaExporter.ExportAsync(info, temp, 0, 0.5, bucket, 0.5, 0.5);
        var sampleInfo = await MediaProbe.ReadAsync(sample.Path);
        Require(sampleInfo.Width == bucket.Width && sampleInfo.Height == bucket.Height,
            "exact size for " + bucket.Name);
    }
    await CaptionFiles.SaveAsync(left.Path, "A red and blue frame with a tone.");
    string leftPrefix = Path.Combine(temp, Path.GetFileNameWithoutExtension(left.Path));
    Require(File.Exists(leftPrefix + ".txt"), "caption pair exists");
    await CaptionFiles.SaveAsync(left.Path, "A two-color frame appears with a steady tone.");
    Require((await File.ReadAllTextAsync(leftPrefix + ".txt")).Trim() ==
            "A two-color frame appears with a steady tone.", "edited caption replaces draft");
    using (var canceled = new CancellationTokenSource())
    {
        canceled.Cancel();
        try
        {
            await CaptionFiles.SaveAsync(left.Path, "This must not replace the previous caption.", canceled.Token);
            throw new Exception("Canceled caption save unexpectedly completed.");
        }
        catch (OperationCanceledException) { }
    }
    Require((await File.ReadAllTextAsync(leftPrefix + ".txt")).Trim() ==
            "A two-color frame appears with a steady tone.", "canceled caption save preserves previous text");
    Require(Directory.GetFiles(temp, Path.GetFileNameWithoutExtension(left.Path) + ".*").Length == 2,
        "only media and caption are saved");

    string multiAudioSource = Path.Combine(temp, "multi-audio.mp4");
    await Run("ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
        "-f", "lavfi", "-i", "color=c=green:s=160x90:r=24:d=2",
        "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
        "-f", "lavfi", "-i", "sine=frequency=880:duration=2",
        "-map", "0:v:0", "-map", "1:a:0", "-map", "2:a:0",
        "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", multiAudioSource);
    Require(await AudioStreamCount(multiAudioSource) == 2, "multi-audio source fixture");
    var multiAudioInfo = await MediaProbe.ReadAsync(multiAudioSource);
    var singleTrack = await MediaExporter.ExportAsync(multiAudioInfo, temp, 0, multiAudioInfo.Duration,
        Buckets.Nearest(multiAudioInfo.Width, multiAudioInfo.Height), 0.5, 0.5);
    Require(await AudioStreamCount(singleTrack.Path) == 1, "only the first audio track is exported");

    string silentVideoSource = Path.Combine(temp, "silent-video.mp4");
    await Run("ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
        "color=c=black:s=160x90:r=24:d=1", "-c:v", "libx264", "-pix_fmt", "yuv420p", silentVideoSource);
    var silentVideoInfo = await MediaProbe.ReadAsync(silentVideoSource);
    Require(ProjectMediaPolicyRules.GetRejection(ProjectMediaPolicy.JointVideoAudio,
                silentVideoInfo.HasVideo, silentVideoInfo.HasAudio)?.Contains("audio") == true &&
            ProjectMediaPolicyRules.GetRejection(ProjectMediaPolicy.VideoOnly,
                silentVideoInfo.HasVideo, silentVideoInfo.HasAudio) is null,
        "batch policy skips no-audio video in joint projects but accepts it in video-only projects");
    var silentVideoExport = await MediaExporter.ExportAsync(silentVideoInfo, temp, 0, silentVideoInfo.Duration,
        Buckets.Nearest(silentVideoInfo.Width, silentVideoInfo.Height), 0.5, 0.5);
    Require(!silentVideoInfo.HasAudio && await AudioStreamCount(silentVideoExport.Path) == 0,
        "silent video exports without inventing an audio track");

    string anamorphicSource = Path.Combine(temp, "anamorphic.mp4");
    await Run("ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
        "testsrc2=s=720x576:r=25:d=2", "-vf", "setsar=16/11", "-c:v", "libx264",
        "-pix_fmt", "yuv420p", anamorphicSource);
    var anamorphicInfo = await MediaProbe.ReadAsync(anamorphicSource);
    Require(anamorphicInfo.Width is >= 1046 and <= 1048 && anamorphicInfo.Height == 576 &&
            anamorphicInfo.FrameRate == 25, "anamorphic probe uses display aspect and source frame rate");
    var anamorphicBucket = Buckets.Nearest(anamorphicInfo.Width, anamorphicInfo.Height);
    Require(anamorphicBucket.Name == "7:4", "anamorphic bucket follows displayed shape");
    byte[] preview = await MediaExporter.FrameAsync(anamorphicSource, 0.5);
    int previewWidth = BinaryPrimitives.ReadInt32BigEndian(preview.AsSpan(16, 4));
    int previewHeight = BinaryPrimitives.ReadInt32BigEndian(preview.AsSpan(20, 4));
    Require(Math.Abs((double)previewWidth / previewHeight -
                     (double)anamorphicInfo.Width / anamorphicInfo.Height) < 0.02,
        "anamorphic crop preview matches displayed shape");
    var anamorphicExport = await MediaExporter.ExportAsync(anamorphicInfo, temp, 0, 1,
        anamorphicBucket, 0.5, 0.5);
    var anamorphicOutputInfo = await MediaProbe.ReadAsync(anamorphicExport.Path);
    Require(anamorphicOutputInfo.Width == anamorphicBucket.Width &&
            anamorphicOutputInfo.Height == anamorphicBucket.Height,
        "anamorphic export has square pixels at bucket dimensions");

    string audioSource = Path.Combine(temp, "audio.wav");
    await Run("ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
        "sine=frequency=660:sample_rate=16000", "-t", "4", audioSource);
    var audioInfo = await MediaProbe.ReadAsync(audioSource);
    Require(ProjectMediaPolicyRules.GetRejection(ProjectMediaPolicy.JointVideoAudio,
                audioInfo.HasVideo, audioInfo.HasAudio)?.Contains("video") == true &&
            ProjectMediaPolicyRules.GetRejection(ProjectMediaPolicy.VideoOnly,
                audioInfo.HasVideo, audioInfo.HasAudio)?.Contains("video") == true &&
            ProjectMediaPolicyRules.GetRejection(ProjectMediaPolicy.LegacyMixed,
                audioInfo.HasVideo, audioInfo.HasAudio) is null,
        "new video project types skip audio-only sources; existing mixed projects keep them");
    var audioExport = await MediaExporter.ExportAsync(audioInfo, temp, 0.5, 2.5, null, 0.5, 0.5);
    var exportedAudioInfo = await MediaProbe.ReadAsync(audioExport.Path);
    Require(exportedAudioInfo.HasAudio && !exportedAudioInfo.HasVideo, "audio-only export");
    Require(Path.GetFileName(audioExport.Path).StartsWith("Vid-", StringComparison.Ordinal) &&
            Path.GetExtension(audioExport.Path) == ".flac", "audio-only output joins the numbered sequence");
    Require((await MediaExporter.WaveformAsync(audioSource, 2, audioInfo.Duration)).Length > 1000,
        "audio waveform preview");

    string longSource = Path.Combine(temp, "long.mp4");
    await Run("ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
        "-f", "lavfi", "-i", "color=c=green:s=128x72:r=1",
        "-f", "lavfi", "-i", "sine=frequency=330:sample_rate=8000",
        "-t", "120", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", longSource);
    var longInfo = await MediaProbe.ReadAsync(longSource);
    var late = await MediaExporter.ExportAsync(longInfo, temp, 115, 117, Buckets.Nearest(128, 72), 0.5, 0.5);
    Require(Math.Abs((await MediaProbe.ReadAsync(late.Path)).Duration - 2) < 0.15, "late-range seek");

    string preexistingCaption = Path.Combine(temp, "Vid-0400.txt");
    await File.WriteAllTextAsync(preexistingCaption, "Existing caption must survive.");
    var bumped = await MediaExporter.ExportAsync(audioInfo, temp, 0.5, 1.5, null, 0.5, 0.5);
    Require(Path.GetFileName(bumped.Path) == "Vid-0401.flac" &&
            await File.ReadAllTextAsync(preexistingCaption) == "Existing caption must survive.",
        "a preexisting text file reserves its number across media extensions");
    File.Delete(bumped.Path);
    var concurrent = await Task.WhenAll(
        MediaExporter.ExportAsync(audioInfo, temp, 0.5, 1.5, null, 0.5, 0.5),
        MediaExporter.ExportAsync(audioInfo, temp, 1.5, 2.5, null, 0.5, 0.5));
    Require(concurrent.Select(result => Path.GetFileName(result.Path)).OrderBy(name => name)
            .SequenceEqual(["Vid-0402.flac", "Vid-0403.flac"]),
        "concurrent exports get distinct numbers and a deleted high number is not reused");
    string stateDirectory = Path.Combine(temp, ".caption-express");
    Require((await File.ReadAllTextAsync(Path.Combine(stateDirectory, "next-id"))).Trim() == "404" &&
            Directory.GetFiles(stateDirectory, "*.partial.*").Length == 0,
        "the next number persists without leaving partial media");
    Console.WriteLine("Media smoke test passed: buckets, crop, numbered pairs, concurrent exports, audio, late seek, original preservation.");
}
finally
{
    Directory.Delete(temp, recursive: true);
}

static async Task Run(string file, params string[] arguments)
{
    var start = new ProcessStartInfo(file)
    {
        UseShellExecute = false, RedirectStandardError = true
    };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    string error = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new Exception(error);
}

static Task DecodeVideoAndAudio(string file) => Run("ffmpeg", "-hide_banner", "-loglevel", "error",
    "-xerror", "-i", file, "-map", "0:v:0", "-map", "0:a:0", "-f", "null", "-");

static async Task<byte[]> CenterPixel(string file)
{
    var start = new ProcessStartInfo("ffmpeg")
    {
        UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
    };
    foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-i", file,
                 "-frames:v", "1", "-vf", "crop=2:2:512:512,format=rgb24", "-f", "rawvideo", "pipe:1" })
        start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    using var output = new MemoryStream();
    await process.StandardOutput.BaseStream.CopyToAsync(output);
    string error = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0 || output.Length != 12) throw new Exception(error);
    return output.ToArray()[..3];
}

static async Task<(double video, double audio)> StreamDurations(string file)
{
    var start = new ProcessStartInfo("ffprobe")
    {
        UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
    };
    foreach (var argument in new[] { "-v", "error", "-show_entries", "stream=codec_type,duration",
                 "-of", "json", file }) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    string output = await process.StandardOutput.ReadToEndAsync();
    string error = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new Exception(error);
    using var document = JsonDocument.Parse(output);
    var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
    double Get(string kind) => double.Parse(streams.Single(item => item.GetProperty("codec_type").GetString() == kind)
        .GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
    return (Get("video"), Get("audio"));
}

static async Task<(string video, string audio)> StreamCodecs(string file)
{
    var start = new ProcessStartInfo("ffprobe")
    {
        UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
    };
    foreach (var argument in new[] { "-v", "error", "-show_entries", "stream=codec_type,codec_name",
                 "-of", "json", file }) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    string output = await process.StandardOutput.ReadToEndAsync();
    string error = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new Exception(error);
    using var document = JsonDocument.Parse(output);
    var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
    string Get(string kind) => streams.Single(item => item.GetProperty("codec_type").GetString() == kind)
        .GetProperty("codec_name").GetString()!;
    return (Get("video"), Get("audio"));
}

static async Task<string> VideoPixelFormat(string file)
{
    var start = new ProcessStartInfo("ffprobe")
    {
        RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
    };
    foreach (var argument in new[] { "-v", "error", "-select_streams", "v:0", "-show_entries",
                 "stream=pix_fmt", "-of", "default=noprint_wrappers=1:nokey=1", file })
        start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new Exception("Could not start ffprobe.");
    string output = await process.StandardOutput.ReadToEndAsync();
    string error = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new Exception("ffprobe failed: " + error);
    return output.Trim();
}

static async Task<int> AudioStreamCount(string file)
{
    var start = new ProcessStartInfo("ffprobe")
    {
        UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
    };
    foreach (var argument in new[] { "-v", "error", "-show_entries", "stream=codec_type",
                 "-of", "json", file }) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    string output = await process.StandardOutput.ReadToEndAsync();
    string error = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new Exception(error);
    using var document = JsonDocument.Parse(output);
    return document.RootElement.GetProperty("streams").EnumerateArray()
        .Count(item => item.GetProperty("codec_type").GetString() == "audio");
}

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception("Failed: " + message);
}
