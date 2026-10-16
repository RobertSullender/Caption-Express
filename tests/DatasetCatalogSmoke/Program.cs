using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CaptionExpress.Dataset;

if (args.Length == 4 && args[0] == "--record")
{
    await new DatasetCatalog(args[1]).RecordExportAsync(args[2], args[3]);
    return;
}

string temp = Path.Combine(Path.GetTempPath(), "caption-catalog-test-" + Guid.NewGuid().ToString("N"));
string exports = Path.Combine(temp, "exports");
Directory.CreateDirectory(exports);
try
{
    string sourceA = Path.Combine(temp, "source-a.mp4");
    string sourceB = Path.Combine(temp, "renamed-copy.mp4");
    string changed = Path.Combine(temp, "changed.mp4");
    await File.WriteAllBytesAsync(sourceA, [1, 2, 3, 4]);
    await File.WriteAllBytesAsync(sourceB, [1, 2, 3, 4]);
    await File.WriteAllBytesAsync(changed, [1, 2, 3, 5]);
    var catalog = new DatasetCatalog(exports + Path.DirectorySeparatorChar);
    string hashA = await catalog.ComputeSourceHashAsync(sourceA);
    string hashB = await catalog.ComputeSourceHashAsync(sourceB);
    string hashChanged = await catalog.ComputeSourceHashAsync(changed);
    Require(hashA == Convert.ToHexStringLower(SHA256.HashData([1, 2, 3, 4])) && hashA == hashB,
        "identical source bytes have the same streaming hash across names");
    Require(hashA != hashChanged && !await catalog.ContainsSourceHashAsync(hashA),
        "one changed byte is not an exact duplicate");

    string media = Path.Combine(exports, "Vid-0001.mp4");
    await File.WriteAllBytesAsync(media, [9, 8, 7]);
    await catalog.RecordExportAsync(media, hashA);
    Require(!await catalog.ContainsSourceHashAsync(hashB) &&
            await catalog.FindIncompleteExportByHashAsync(hashB) is null,
        "uncaptioned one-clip export is not commandeered by batch resume");

    string batchExports = Path.Combine(temp, "batch-exports");
    Directory.CreateDirectory(batchExports);
    string unfinishedBatch = Path.Combine(batchExports, "Vid-0002.mp4");
    await File.WriteAllBytesAsync(unfinishedBatch, [7, 8, 9]);
    var batchCatalog = new DatasetCatalog(batchExports);
    await batchCatalog.RecordExportAsync(unfinishedBatch, hashB, batchExport: true);
    Require(!await batchCatalog.ContainsSourceHashAsync(hashB) &&
            (await batchCatalog.FindIncompleteExportByHashAsync(hashB))?.MediaPath == unfinishedBatch,
        "uncaptioned batch export can resume without making another video");
    Require((await batchCatalog.FindIncompleteExportByHashAsync(hashB,
                videoFormatId: "h264-mp4"))?.MediaPath == unfinishedBatch &&
            await batchCatalog.FindIncompleteExportByHashAsync(hashB,
                videoFormatId: "hevc-mp4") is null &&
            await batchCatalog.FindIncompleteExportByHashAsync(hashB,
                videoFormatId: "av1-mp4") is null,
        "legacy batch MP4 resumes only when H.264 is selected");
    await batchCatalog.SetReviewStatusAsync(unfinishedBatch, DatasetReviewStatus.Pending);
    await batchCatalog.RecordExportAsync(unfinishedBatch, hashB);
    Require((await new DatasetCatalog(batchExports).FindIncompleteExportByHashAsync(hashB))?.MediaPath ==
            unfinishedBatch, "batch provenance survives status changes, repeat record, and restart");

    string hevcBatch = Path.Combine(batchExports, "Vid-0004.mp4");
    string hevcHash = Convert.ToHexStringLower(SHA256.HashData([8, 7, 6, 5]));
    await File.WriteAllBytesAsync(hevcBatch, [8, 7, 6, 5]);
    await batchCatalog.RecordExportAsync(hevcBatch, hevcHash, batchExport: true,
        videoFormatId: "hevc-mp4");
    await batchCatalog.SetReviewStatusAsync(hevcBatch, DatasetReviewStatus.Pending);
    await batchCatalog.RecordExportAsync(hevcBatch, hevcHash);
    var restartedCatalog = new DatasetCatalog(batchExports);
    Require((await restartedCatalog.FindIncompleteExportByHashAsync(hevcHash,
                videoFormatId: "hevc-mp4"))?.MediaPath == hevcBatch &&
            await restartedCatalog.FindIncompleteExportByHashAsync(hevcHash,
                videoFormatId: "h264-mp4") is null &&
            await restartedCatalog.FindIncompleteExportByHashAsync(hevcHash,
                videoFormatId: "av1-mp4") is null,
        "selected MP4 codec survives review edits, repeat record, and restart");

    string unfinishedAudio = Path.Combine(batchExports, "Vid-0005.flac");
    string audioHash = Convert.ToHexStringLower(SHA256.HashData([4, 6, 8]));
    await File.WriteAllBytesAsync(unfinishedAudio, [4, 6, 8]);
    await batchCatalog.RecordExportAsync(unfinishedAudio, audioHash, batchExport: true);
    Require((await batchCatalog.FindIncompleteExportByHashAsync(audioHash,
                videoFormatId: "av1-mp4"))?.MediaPath == unfinishedAudio,
        "audio-only batch resume is independent of the selected video codec");
    await File.WriteAllTextAsync(Path.ChangeExtension(unfinishedBatch, ".txt"), "Complete batch caption.\n");
    Require(await batchCatalog.ContainsSourceHashAsync(hashB), "captioned batch export blocks exact duplicate");
    await batchCatalog.SetReviewStatusAsync(unfinishedBatch, DatasetReviewStatus.Reviewed);

    string oneClipInBatchFolder = Path.Combine(batchExports, "Vid-0003.mp4");
    await File.WriteAllBytesAsync(oneClipInBatchFolder, [3, 3, 3]);
    await batchCatalog.RecordExportAsync(oneClipInBatchFolder, hashChanged);
    await File.WriteAllTextAsync(Path.ChangeExtension(oneClipInBatchFolder, ".txt"), "A custom trim.\n");
    await batchCatalog.RecordPublishedBatchHashesAsync([unfinishedBatch, oneClipInBatchFolder]);
    await batchCatalog.RecordPublishedBatchHashesAsync([unfinishedBatch, oneClipInBatchFolder]);
    using (JsonDocument catalogJson = JsonDocument.Parse(await File.ReadAllTextAsync(
               Path.Combine(batchExports, ".caption-express", "catalog.json"))))
        Require(catalogJson.RootElement.GetProperty("sourceHashes").GetArrayLength() == 1,
            "publishing the same batch hash twice is idempotent");
    Require(!await batchCatalog.ContainsSourceHashAsync(hashChanged),
        "published hash recording ignores one-clip trims in the same exports folder");
    Require((await batchCatalog.ListPairsAsync()).Single(pair => pair.MediaPath == unfinishedBatch)
            .ReviewStatus == DatasetReviewStatus.Reviewed,
        "publishing batch hashes leaves active export review state intact");
    File.Delete(unfinishedBatch);
    File.Delete(Path.ChangeExtension(unfinishedBatch, ".txt"));
    Require(await batchCatalog.ContainsSourceHashAsync(hashB),
        "published batch hash survives interrupted cleanup before active record removal");
    await batchCatalog.RemoveExportAsync(unfinishedBatch);
    Require(await new DatasetCatalog(batchExports).ContainsSourceHashAsync(hashB),
        "finalized batch source remains in duplicate history after active record removal");
    Require((await catalog.ListPairsAsync()).Single().ReviewStatus == DatasetReviewStatus.Pending,
        "new export starts pending without a caption");
    try
    {
        await catalog.SetReviewStatusAsync(media, DatasetReviewStatus.Reviewed);
        throw new Exception("Reviewed status unexpectedly accepted a missing caption.");
    }
    catch (InvalidOperationException) { }
    await File.WriteAllTextAsync(Path.ChangeExtension(media, ".txt"), "A short reviewed description.\n");
    await catalog.SetReviewStatusAsync(media, DatasetReviewStatus.Reviewed);
    string initialCaptionHash = Convert.ToHexStringLower(SHA256.HashData(
        await File.ReadAllBytesAsync(Path.ChangeExtension(media, ".txt"))));
    Require((await catalog.ListPairsAsync()).Single(pair => pair.MediaPath == media).ReviewedCaptionHash ==
            initialCaptionHash, "review records the exact saved text bytes");
    await catalog.RecordExportAsync(media, hashA);
    Require((await catalog.ListPairsAsync()).Single(pair => pair.MediaPath == media).ReviewedCaptionHash ==
            initialCaptionHash, "repeat record for the same source keeps review text integrity");
    await File.WriteAllTextAsync(Path.ChangeExtension(media, ".txt"), "Externally edited caption.\n");
    var externallyEdited = (await catalog.ListPairsAsync()).Single(pair => pair.MediaPath == media);
    Require(externallyEdited.ReviewStatus == DatasetReviewStatus.Pending &&
            externallyEdited.ReviewedCaptionHash is null,
        "external caption edits invalidate reviewed status until reviewed again");
    await catalog.SetReviewStatusAsync(media, DatasetReviewStatus.Reviewed);
    Require((await catalog.ListPairsAsync()).Single(pair => pair.MediaPath == media).ReviewStatus ==
            DatasetReviewStatus.Reviewed, "edited caption can be reviewed again");
    File.Delete(Path.ChangeExtension(media, ".txt"));
    var missingCaption = (await catalog.ListPairsAsync()).Single(pair => pair.MediaPath == media);
    Require(missingCaption.ReviewStatus == DatasetReviewStatus.Pending && missingCaption.ReviewedCaptionHash is null,
        "missing reviewed caption reverts to pending");
    await File.WriteAllTextAsync(Path.ChangeExtension(media, ".txt"), "A restored caption.\n");
    await catalog.SetReviewStatusAsync(media, DatasetReviewStatus.Reviewed);
    Require(!await catalog.ContainsSourceHashAsync(hashB) &&
            await catalog.FindIncompleteExportByHashAsync(hashB) is null,
        "captioned one-clip trim does not mark the full source processed");

    string legacy = Path.Combine(exports, "old-export.webm");
    await File.WriteAllBytesAsync(legacy, [4, 5, 6]);
    await File.WriteAllTextAsync(Path.ChangeExtension(legacy, ".txt"), "Legacy caption.\n");
    string audio = Path.Combine(exports, "Vid-0002.flac");
    await File.WriteAllBytesAsync(audio, [2, 5, 4]);
    var afterRestart = new DatasetCatalog(exports);
    var pairs = await afterRestart.ListPairsAsync();
    Require(pairs.Count == 3 && pairs.Single(pair => pair.MediaPath == media).ReviewStatus == DatasetReviewStatus.Reviewed,
        "review state survives a new catalog instance");
    Require(pairs.Single(pair => pair.MediaPath == legacy).ReviewStatus == DatasetReviewStatus.Pending &&
            pairs.Single(pair => pair.MediaPath == legacy).CaptionPath == Path.ChangeExtension(legacy, ".txt"),
        "legacy video/text pair is found after restart");
    Require(!pairs.Single(pair => pair.MediaPath == audio).HasVideo &&
            pairs.Single(pair => pair.MediaPath == audio).CaptionPath is null,
        "audio-only export is visible but marked as nonvideo");
    await afterRestart.SetReviewStatusAsync(audio, DatasetReviewStatus.Excluded);
    Require((await afterRestart.ListPairsAsync()).Single(pair => pair.MediaPath == audio).ReviewStatus ==
            DatasetReviewStatus.Excluded, "excluded status persists without a text pair");

    string crossExports = Path.Combine(temp, "concurrent-exports");
    Directory.CreateDirectory(crossExports);
    var childPaths = new List<(string Media, string Hash)>();
    var childProcesses = new List<Process>();
    for (int index = 0; index < 6; index++)
    {
        string childMedia = Path.Combine(crossExports, $"Vid-{index + 1:D4}.mp4");
        byte[] content = [(byte)index, 42];
        await File.WriteAllBytesAsync(childMedia, content);
        string childHash = Convert.ToHexStringLower(SHA256.HashData(content));
        childPaths.Add((childMedia, childHash));
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        start.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--record");
        start.ArgumentList.Add(crossExports);
        start.ArgumentList.Add(childMedia);
        start.ArgumentList.Add(childHash);
        childProcesses.Add(Process.Start(start) ?? throw new Exception("Could not start catalog writer."));
    }
    await Task.WhenAll(childProcesses.Select(process => process.WaitForExitAsync()));
    Require(childProcesses.All(process => process.ExitCode == 0), "all concurrent writers finish");
    foreach (Process process in childProcesses) process.Dispose();
    var crossPairs = await new DatasetCatalog(crossExports).ListPairsAsync();
    Require(crossPairs.Count == childPaths.Count && childPaths.All(item =>
            crossPairs.Any(pair => pair.MediaPath == item.Media && pair.SourceHash == item.Hash)),
        "concurrent app processes preserve every catalog entry");

    await afterRestart.RemoveExportAsync(media);
    Require(!await afterRestart.ContainsSourceHashAsync(hashA),
        "finalizing a one-clip trim does not block a later full-source folder run");
    Require((await afterRestart.ListPairsAsync()).Single(pair => pair.MediaPath == media).ReviewStatus ==
            DatasetReviewStatus.Pending, "unregistered surviving media is rediscovered as a legacy pair");
    Require(Directory.GetFiles(Path.Combine(exports, ".caption-express"), ".catalog-*.tmp").Length == 0,
        "atomic catalog writes leave no temporary files");
    Console.WriteLine("Dataset catalog smoke test passed.");
}
finally
{
    Directory.Delete(temp, recursive: true);
}

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception("Dataset catalog check failed: " + message);
}
