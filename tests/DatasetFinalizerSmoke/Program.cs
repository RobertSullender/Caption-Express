using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CaptionExpress.Dataset;
using CaptionExpress.Media;

string root = Path.Combine(Path.GetTempPath(), "caption-express-dataset-" + Guid.NewGuid().ToString("N"));
string exports = Path.Combine(root, "exports");
Directory.CreateDirectory(exports);
try
{
    string firstVideo = Path.Combine(exports, "Vid-0001.mp4");
    string firstCaption = Path.Combine(exports, "Vid-0001.txt");
    await MakeVideoAsync(firstVideo);
    string reviewedCaption = "A woman says \"hello\".\nA path looks like C:\\clips\\one; café 🎵.";
    await File.WriteAllTextAsync(firstCaption, reviewedCaption + "\n");
    byte[] videoHash = SHA256.HashData(await File.ReadAllBytesAsync(firstVideo));
    string secondVideo = Path.Combine(exports, "Vid-0003.webm");
    string secondCaption = Path.Combine(exports, "Vid-0003.txt");
    await MakeWebmVideoAsync(secondVideo);
    await File.WriteAllTextAsync(secondCaption, "A blue frame appears while a tone plays.\n");
    string thirdVideo = Path.Combine(exports, "Vid-0004.mov");
    string thirdCaption = Path.Combine(exports, "Vid-0004.txt");
    await MakeMovVideoAsync(thirdVideo);
    await File.WriteAllTextAsync(thirdCaption, "A blue frame appears with a sustained tone.\n");
    string batchHash = Convert.ToHexStringLower(videoHash);
    string oneClipHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(secondVideo)));
    byte[] movHash = SHA256.HashData(await File.ReadAllBytesAsync(thirdVideo));
    string movBatchHash = Convert.ToHexStringLower(movHash);
    var catalog = new DatasetCatalog(exports);
    await catalog.RecordExportAsync(firstVideo, batchHash, batchExport: true);
    await catalog.RecordExportAsync(secondVideo, oneClipHash);
    await catalog.RecordExportAsync(thirdVideo, movBatchHash, batchExport: true);
    await catalog.SetReviewStatusAsync(firstVideo, DatasetReviewStatus.Reviewed);
    await catalog.SetReviewStatusAsync(secondVideo, DatasetReviewStatus.Reviewed);
    await catalog.SetReviewStatusAsync(thirdVideo, DatasetReviewStatus.Reviewed);
    var reviewedPairs = await catalog.ListPairsAsync();
    var firstReviewed = reviewedPairs.Single(pair => pair.MediaPath == firstVideo);
    var secondReviewed = reviewedPairs.Single(pair => pair.MediaPath == secondVideo);
    var thirdReviewed = reviewedPairs.Single(pair => pair.MediaPath == thirdVideo);
    Require(!string.IsNullOrWhiteSpace(firstReviewed.ReviewedCaptionHash) &&
            !string.IsNullOrWhiteSpace(secondReviewed.ReviewedCaptionHash) &&
            !string.IsNullOrWhiteSpace(thirdReviewed.ReviewedCaptionHash),
        "catalog provides hashes for the exact reviewed caption bytes");
    string nextId = Path.Combine(exports, ".caption-express", "next-id");
    await File.WriteAllTextAsync(nextId, "5\n");

    string sharedStemVideo = Path.Combine(exports, "Vid-0001.mov");
    File.Copy(thirdVideo, sharedStemVideo);
    string sharedStemDestination = Path.Combine(root, "finalized", "shared-caption");
    await ExpectFailureAsync(() => DatasetFinalizer.FinalizeAsync(
        [firstReviewed], sharedStemDestination),
        "another working media file sharing the selected text basename blocks cleanup");
    Require(File.Exists(firstVideo) && File.Exists(firstCaption) &&
            File.Exists(sharedStemVideo) && !Directory.Exists(sharedStemDestination),
        "ambiguous caption ownership preserves both media files and the shared text");
    File.Delete(sharedStemVideo);

    string unreviewedVideo = Path.Combine(exports, "Vid-0002.mp4");
    string unreviewedCaption = Path.Combine(exports, "Vid-0002.txt");
    File.Copy(firstVideo, unreviewedVideo);
    await File.WriteAllTextAsync(unreviewedCaption, "Still being reviewed.\n");
    string unrelated = Path.Combine(exports, "notes.txt");
    await File.WriteAllTextAsync(unrelated, "Do not delete me.");

    string interrupted = Path.Combine(root, "finalized", "interrupted");
    await ExpectFailureAsync(() => DatasetFinalizer.FinalizeAsync(
        [firstReviewed, secondReviewed, thirdReviewed], interrupted,
        progress: new ThrowAfterFirstPair()),
        "failure after staging the first pair is rolled back");
    Require(File.Exists(firstVideo) && File.Exists(firstCaption) &&
            File.Exists(secondVideo) && File.Exists(secondCaption) &&
            File.Exists(thirdVideo) && File.Exists(thirdCaption) &&
            !Directory.Exists(interrupted) &&
            !Directory.GetDirectories(Path.GetDirectoryName(interrupted)!, ".*.partial-*").Any(),
        "partial failure preserves all sources and removes the incomplete stage");

    string destination = Path.Combine(root, "finalized", "dataset-001");
    var result = await DatasetFinalizer.FinalizeAsync(
        [firstReviewed, secondReviewed, thirdReviewed], destination);
    Require(result.PairCount == 3 && result.DirectoryPath == destination &&
            result.ManifestPath == Path.Combine(destination, "dataset.jsonl") &&
            result.RetainedSourcePaths.Count == 0 && result.CleanupWarning is null &&
            result.VideosWithoutAudio == 0,
        "result describes the published dataset without a cleanup warning");
    string recipePath = Path.Combine(destination, "training-recipe.sh");
    Require(File.Exists(recipePath), "the published dataset includes its LTX training recipe");
    string recipe = await File.ReadAllTextAsync(recipePath);
    Require(recipe.Contains("dataset.jsonl", StringComparison.Ordinal) &&
            recipe.Contains("128x128x25", StringComparison.Ordinal),
        "training recipe names the published manifest and the exact video bucket");
    Require(recipe.Contains("Embedded audio streams: 3 with audio; 0 without audio.", StringComparison.Ordinal) &&
            !recipe.Contains("WARNING: Videos without audio", StringComparison.Ordinal),
        "training recipe accurately counts audio streams in the published videos");
    string sharedRatioRecipe = LtxTrainingRecipe.Render(
        [new(128, 128, 25, 24, true), new(256, 256, 25, 24, true)]);
    Require(sharedRatioRecipe.Contains("Spatial warning", StringComparison.Ordinal) &&
            sharedRatioRecipe.Contains("resizes smaller clips", StringComparison.Ordinal),
        "recipe warns when LTX would resize clips sharing an aspect ratio and frame count");
    string videoWithoutAudio = Path.Combine(root, "silent-video.mp4");
    await MakeVideoWithoutAudioAsync(videoWithoutAudio);
    var silentShape = await LtxTrainingRecipe.InspectAsync(videoWithoutAudio);
    Require(!silentShape.HasAudio && silentShape.Bucket == "128x128x25",
        "recipe inspection detects a valid video without an embedded audio stream");
    string mixedAudioRecipe = LtxTrainingRecipe.Render(
        [await LtxTrainingRecipe.InspectAsync(Path.Combine(destination, "Vid-0001.mp4")), silentShape]);
    Require(mixedAudioRecipe.Contains("1 with audio; 1 without audio", StringComparison.Ordinal) &&
            mixedAudioRecipe.Contains("joint audio+video training excludes", StringComparison.Ordinal) &&
            mixedAudioRecipe.Contains("omit training_strategy.audio", StringComparison.Ordinal),
        "recipe explains how the stock joint trainer handles videos without audio");
    string jointRecipe = LtxTrainingRecipe.Render(
        [new(128, 128, 25, 24, true)], ProjectMediaPolicy.JointVideoAudio);
    Require(jointRecipe.Contains("Project type: joint video + audio", StringComparison.Ordinal) &&
            !jointRecipe.Contains("--skip-audio", StringComparison.Ordinal),
        "joint training recipe retains audio preprocessing");
    string videoOnlyRecipe = LtxTrainingRecipe.Render(
        [silentShape], ProjectMediaPolicy.VideoOnly);
    Require(videoOnlyRecipe.Contains("--skip-audio", StringComparison.Ordinal) &&
            videoOnlyRecipe.Contains("Remove training_strategy.audio", StringComparison.Ordinal),
        "video-only recipe avoids unused audio latents and instructs a video-only trainer config");
    await ExpectFailureAsync(() => Task.FromResult(LtxTrainingRecipe.Render(
        [silentShape], ProjectMediaPolicy.JointVideoAudio)),
        "joint recipe refuses a video with no audio stream");
    await ExpectFailureAsync(() => Task.FromResult(LtxTrainingRecipe.Render(
        [new(128, 128, 25, 24, true)], ProjectMediaPolicy.VideoOnly)),
        "video-only recipe refuses a video with an audio stream");
    string silentExports = Path.Combine(root, "silent-exports");
    Directory.CreateDirectory(silentExports);
    string silentExport = Path.Combine(silentExports, "Vid-0001.mp4");
    string silentCaption = Path.Combine(silentExports, "Vid-0001.txt");
    File.Copy(videoWithoutAudio, silentExport);
    await File.WriteAllTextAsync(silentCaption, "A blue frame remains visible.");
    var silentCatalog = new DatasetCatalog(silentExports);
    await silentCatalog.SetReviewStatusAsync(silentExport, DatasetReviewStatus.Reviewed);
    var silentPairs = await silentCatalog.ListPairsAsync();
    await ExpectFailureAsync(() => DatasetFinalizer.FinalizeAsync(silentPairs,
        Path.Combine(root, "finalized", "silent-joint-rejected"),
        mediaPolicy: ProjectMediaPolicy.JointVideoAudio),
        "joint project refuses no-audio video before dataset publication");
    var silentResult = await DatasetFinalizer.FinalizeAsync(silentPairs,
        Path.Combine(root, "finalized", "silent-dataset"),
        mediaPolicy: ProjectMediaPolicy.VideoOnly);
    Require(silentResult.VideosWithoutAudio == 1 &&
            (await File.ReadAllTextAsync(silentResult.RecipePath)).Contains(
                "--skip-audio", StringComparison.Ordinal),
        "video-only finalization accepts no-audio video and proposes no audio preprocessing");
    using (var bash = Process.Start(new ProcessStartInfo("bash")
           {
               ArgumentList = { "-n", recipePath },
               RedirectStandardError = true,
               UseShellExecute = false
           }) ?? throw new Exception("Could not start bash for recipe validation."))
    {
        string bashError = await bash.StandardError.ReadToEndAsync();
        await bash.WaitForExitAsync();
        Require(bash.ExitCode == 0, "published training recipe has valid Bash syntax: " + bashError);
    }
    Require(!File.Exists(firstVideo) && !File.Exists(firstCaption) &&
            !File.Exists(secondVideo) && !File.Exists(secondCaption) &&
            !File.Exists(thirdVideo) && !File.Exists(thirdCaption),
        "only published pairs are removed from working exports");
    Require(File.Exists(unreviewedVideo) && File.Exists(unreviewedCaption) && File.Exists(unrelated),
        "unreviewed and unrelated exports stay intact");
    Require(File.Exists(nextId) && (await File.ReadAllTextAsync(nextId)).Trim() == "5",
        "the hidden export counter remains intact");
    Require(await catalog.ContainsSourceHashAsync(batchHash) &&
            await catalog.ContainsSourceHashAsync(movBatchHash) &&
            !await catalog.ContainsSourceHashAsync(oneClipHash),
        "a finalized batch hash is durable while one-clip exports do not block later batch runs");
    Require(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(destination, "Vid-0001.mp4")))
            .SequenceEqual(videoHash), "published video is an exact copy");
    Require((await File.ReadAllTextAsync(Path.Combine(destination, "Vid-0001.txt"))).Trim() == reviewedCaption,
        "published text preserves the reviewed caption");
    Require((await MediaProbe.ReadAsync(Path.Combine(destination, "Vid-0001.mp4"))).HasAudio,
        "embedded audio survives publication");
    Require(File.Exists(Path.Combine(destination, "Vid-0003.webm")) &&
            File.Exists(Path.Combine(destination, "Vid-0003.txt")) &&
            (await MediaProbe.ReadAsync(Path.Combine(destination, "Vid-0003.webm"))).HasAudio,
        "WebM pair and its embedded audio survive publication");
    Require(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(destination, "Vid-0004.mov")))
            .SequenceEqual(movHash) &&
            (await MediaProbe.ReadAsync(Path.Combine(destination, "Vid-0004.mov"))).HasAudio &&
            (await File.ReadAllTextAsync(Path.Combine(destination, "Vid-0004.txt"))).Trim() ==
            "A blue frame appears with a sustained tone.",
        "ProRes MOV pair is copied exactly with its audio and reviewed caption");

    string manifest = await File.ReadAllTextAsync(result.ManifestPath);
    string[] rows = manifest.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    Require(rows.Length == 3, "exactly one JSONL row exists for each selected pair");
    using (var document = JsonDocument.Parse(rows[0]))
    {
        var entry = document.RootElement;
        Require(entry.EnumerateObject().Select(x => x.Name).SequenceEqual(["video", "caption"]),
            "manifest contains only the trainer fields");
        Require(entry.GetProperty("video").GetString() == "Vid-0001.mp4" &&
                entry.GetProperty("caption").GetString() == reviewedCaption,
            "relative video path and quotes, newline, backslash, and Unicode caption round-trip");
    }
    using (var document = JsonDocument.Parse(rows[1]))
        Require(document.RootElement.GetProperty("video").GetString() == "Vid-0003.webm" &&
                document.RootElement.GetProperty("caption").GetString() ==
                "A blue frame appears while a tone plays.",
            "second manifest row matches the WebM pair");
    using (var document = JsonDocument.Parse(rows[2]))
        Require(document.RootElement.GetProperty("video").GetString() == "Vid-0004.mov" &&
                document.RootElement.GetProperty("caption").GetString() ==
                "A blue frame appears with a sustained tone.",
            "third manifest row matches the MOV pair");
    Require(!Directory.GetDirectories(Path.GetDirectoryName(destination)!, ".*.partial-*").Any(),
        "staging folder is not left behind");
    string[] publishedFiles = Directory.EnumerateFiles(destination).Select(Path.GetFileName).ToArray()!;
    Require(publishedFiles.Length == 8 &&
            new HashSet<string>(publishedFiles, StringComparer.Ordinal).SetEquals(
                ["Vid-0001.mp4", "Vid-0001.txt", "Vid-0003.webm", "Vid-0003.txt",
                    "Vid-0004.mov", "Vid-0004.txt", "dataset.jsonl", "training-recipe.sh"]),
        "the finalized LTX dataset contains only reviewed pairs, its manifest, and recipe");

    string collision = Path.Combine(root, "finalized", "existing");
    Directory.CreateDirectory(collision);
    string sentinel = Path.Combine(collision, "sentinel");
    await File.WriteAllTextAsync(sentinel, "keep");
    await ExpectFailureAsync(() => DatasetFinalizer.FinalizeAsync(
        [Pair(unreviewedVideo, unreviewedCaption)], collision),
        "destination collision is rejected");
    Require(File.Exists(unreviewedVideo) && File.Exists(unreviewedCaption) && File.Exists(sentinel),
        "collision does not delete or overwrite anything");

    await File.WriteAllTextAsync(unreviewedCaption, " \n\t");
    string emptyDestination = Path.Combine(root, "finalized", "empty-caption");
    await ExpectFailureAsync(() => DatasetFinalizer.FinalizeAsync(
        [Pair(unreviewedVideo, unreviewedCaption)], emptyDestination),
        "empty caption is rejected");
    Require(File.Exists(unreviewedVideo) && File.Exists(unreviewedCaption) &&
            !Directory.Exists(emptyDestination), "invalid caption leaves source intact and no final dataset");

    await File.WriteAllTextAsync(unreviewedCaption, "Reviewed.");
    string missingDestination = Path.Combine(root, "finalized", "missing-caption");
    await ExpectFailureAsync(() => DatasetFinalizer.FinalizeAsync(
        [Pair(unreviewedVideo, Path.Combine(exports, "Vid-0002-missing.txt"))], missingDestination),
        "missing matching caption is rejected");
    Require(File.Exists(unreviewedVideo) && File.Exists(unreviewedCaption) &&
            !Directory.Exists(missingDestination), "failed validation leaves source intact");

    string audioOnly = Path.Combine(exports, "Audio-0001.mp4");
    string audioCaption = Path.Combine(exports, "Audio-0001.txt");
    await MakeAudioOnlyMp4Async(audioOnly);
    await File.WriteAllTextAsync(audioCaption, "A tone plays.");
    string audioDestination = Path.Combine(root, "finalized", "audio-only");
    await ExpectFailureAsync(() => DatasetFinalizer.FinalizeAsync(
        [Pair(audioOnly, audioCaption)], audioDestination),
        "audio-only MP4 is rejected by probing its streams");
    Require(File.Exists(audioOnly) && File.Exists(audioCaption) && !Directory.Exists(audioDestination),
        "audio-only rejection preserves source");

    string invalidFramesVideo = Path.Combine(exports, "Vid-0008.mp4");
    string invalidFramesCaption = Path.Combine(exports, "Vid-0008.txt");
    await MakeInvalidFrameCountVideoAsync(invalidFramesVideo);
    await File.WriteAllTextAsync(invalidFramesCaption, "A blue frame appears with a tone.\n");
    string invalidFramesDestination = Path.Combine(root, "finalized", "invalid-frame-count");
    await ExpectFailureAsync(() => DatasetFinalizer.FinalizeAsync(
        [Pair(invalidFramesVideo, invalidFramesCaption)], invalidFramesDestination),
        "a clip with 24 frames cannot be published for an 8n+1 LTX training window");
    Require(File.Exists(invalidFramesVideo) && File.Exists(invalidFramesCaption) &&
            !Directory.Exists(invalidFramesDestination),
        "frame-window rejection preserves the source pair and publishes no dataset");

    string nested = Path.Combine(exports, "dataset");
    await ExpectFailureAsync(() => DatasetFinalizer.FinalizeAsync(
        [Pair(unreviewedVideo, unreviewedCaption)], nested),
        "final dataset cannot be placed inside working exports");
    Require(File.Exists(unreviewedVideo) && File.Exists(unreviewedCaption),
        "invalid nested destination preserves source");

    await ExpectFailureAsync(() => DatasetFinalizer.FinalizeAsync(
        [new DatasetPair(unreviewedVideo, unreviewedCaption, DatasetReviewStatus.Pending, true, null)],
        Path.Combine(root, "finalized", "pending")),
        "pending pair cannot be finalized");
    Require(File.Exists(unreviewedVideo) && File.Exists(unreviewedCaption),
        "pending rejection preserves source");

    await ExpectFailureAsync(() => DatasetFinalizer.FinalizeAsync(
        [Pair(unreviewedVideo, unreviewedCaption), Pair(unreviewedVideo, unreviewedCaption)],
        Path.Combine(root, "finalized", "duplicate")),
        "duplicate basenames are rejected");
    Require(File.Exists(unreviewedVideo) && File.Exists(unreviewedCaption),
        "duplicate rejection preserves source");

    string reviewedBytesHash = Convert.ToHexStringLower(
        SHA256.HashData(await File.ReadAllBytesAsync(unreviewedCaption)));
    var staleReview = Pair(unreviewedVideo, unreviewedCaption) with
    {
        ReviewedCaptionHash = reviewedBytesHash
    };
    await File.AppendAllTextAsync(unreviewedCaption, "\n");
    string staleDestination = Path.Combine(root, "finalized", "stale-review");
    await ExpectFailureAsync(() => DatasetFinalizer.FinalizeAsync([staleReview], staleDestination),
        "caption byte changes after review are rejected even when trimmed text is unchanged");
    Require(File.Exists(unreviewedVideo) && File.Exists(unreviewedCaption) &&
            !Directory.Exists(staleDestination),
        "stale review cannot publish or delete either working file");

    string brokenExports = Path.Combine(root, "exports-broken-catalog");
    Directory.CreateDirectory(brokenExports);
    string brokenVideo = Path.Combine(brokenExports, "Vid-0007.mp4");
    string brokenCaption = Path.Combine(brokenExports, "Vid-0007.txt");
    File.Copy(unreviewedVideo, brokenVideo);
    await File.WriteAllTextAsync(brokenCaption, "A blue screen with a tone.\n");
    string brokenMetadata = Path.Combine(brokenExports, ".caption-express");
    Directory.CreateDirectory(brokenMetadata);
    await File.WriteAllTextAsync(Path.Combine(brokenMetadata, "catalog.json"), "{broken json");
    string publishedWithWarning = Path.Combine(root, "finalized", "catalog-warning");
    var warningResult = await DatasetFinalizer.FinalizeAsync(
        [Pair(brokenVideo, brokenCaption)], publishedWithWarning);
    Require(Directory.Exists(publishedWithWarning) && File.Exists(warningResult.ManifestPath) &&
            File.Exists(Path.Combine(publishedWithWarning, "Vid-0007.mp4")) &&
            File.Exists(Path.Combine(publishedWithWarning, "Vid-0007.txt")),
        "complete dataset remains published when duplicate history cannot be recorded");
    Require(File.Exists(brokenVideo) && File.Exists(brokenCaption) &&
            warningResult.RetainedSourcePaths.Contains(brokenVideo) &&
            warningResult.RetainedSourcePaths.Contains(brokenCaption) &&
            !string.IsNullOrWhiteSpace(warningResult.CleanupWarning),
        "catalog failure keeps every working source and reports a cleanup warning");

    Console.WriteLine("Dataset finalizer smoke test passed: LTX JSONL and training recipe, copies, duplicate history, selective cleanup, and failure preservation.");
}
finally
{
    Directory.Delete(root, recursive: true);
}

static DatasetPair Pair(string video, string caption) =>
    new(video, caption, DatasetReviewStatus.Reviewed, true, null);

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception("Failed: " + message);
}

static async Task ExpectFailureAsync(Func<Task> action, string message)
{
    try { await action(); }
    catch (ArgumentException) { return; }
    catch (IOException) { return; }
    throw new Exception("Expected failure: " + message);
}

static async Task MakeVideoAsync(string path) => await RunAsync(
    "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=c=blue:s=128x128:r=24:d=2",
    "-f", "lavfi", "-i", "sine=frequency=440:duration=1.041667", "-frames:v", "25",
    "-c:v", "libx264", "-pix_fmt", "yuv420p",
    "-c:a", "aac", path);

static async Task MakeWebmVideoAsync(string path) => await RunAsync(
    "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=c=blue:s=128x128:r=24:d=2",
    "-f", "lavfi", "-i", "sine=frequency=440:duration=1.041667", "-frames:v", "25",
    "-c:v", "libvpx-vp9", "-b:v", "200k",
    "-c:a", "libopus", path);

static async Task MakeMovVideoAsync(string path) => await RunAsync(
    "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=c=blue:s=128x128:r=24:d=2",
    "-f", "lavfi", "-i", "sine=frequency=440:duration=1.041667", "-frames:v", "25",
    "-c:v", "prores_ks", "-profile:v", "2",
    "-pix_fmt", "yuv422p10le", "-c:a", "pcm_s16le", path);

static async Task MakeAudioOnlyMp4Async(string path) => await RunAsync(
    "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=660:duration=1",
    "-c:a", "aac", path);

static async Task MakeVideoWithoutAudioAsync(string path) => await RunAsync(
    "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=c=blue:s=128x128:r=24:d=2",
    "-frames:v", "25", "-c:v", "libx264", "-pix_fmt", "yuv420p", path);

static async Task MakeInvalidFrameCountVideoAsync(string path) => await RunAsync(
    "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=c=blue:s=128x128:r=24:d=1",
    "-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-frames:v", "24",
    "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", path);

static async Task RunAsync(params string[] arguments)
{
    var start = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardError = true };
    foreach (string argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    string error = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new Exception(error);
}

sealed class ThrowAfterFirstPair : IProgress<DatasetFinalizationProgress>
{
    public void Report(DatasetFinalizationProgress value)
    {
        if (value.CompletedPairs == 1) throw new IOException("Injected failure after staging the first pair.");
    }
}
