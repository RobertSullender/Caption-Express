using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CaptionExpress.Media;

namespace CaptionExpress.Dataset;

public sealed record DatasetFinalizationResult(
    string DirectoryPath,
    string ManifestPath,
    string RecipePath,
    int PairCount,
    IReadOnlyList<string> RetainedSourcePaths,
    string? CleanupWarning = null,
    int VideosWithoutAudio = 0);

public sealed record DatasetFinalizationProgress(int CompletedPairs, int TotalPairs, string LastVideoName);

/// <summary>
/// Publishes reviewed video/caption pairs as a training dataset. The source
/// pairs are removed only after a complete dataset is visible at the destination.
/// </summary>
public static class DatasetFinalizer
{
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".webm", ".mov", ".mkv", ".avi",
        ".flac", ".wav", ".mp3", ".m4a", ".ogg", ".opus"
    };

    private sealed record PreparedPair(string VideoPath, string CaptionPath, string VideoName,
        string CaptionName, string Caption, string? ReviewedCaptionHash);

    public static async Task<DatasetFinalizationResult> FinalizeAsync(
        IEnumerable<DatasetPair> pairs,
        string destinationDirectory,
        CancellationToken cancellationToken = default,
        IProgress<DatasetFinalizationProgress>? progress = null,
        ProjectMediaPolicy mediaPolicy = ProjectMediaPolicy.LegacyMixed)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        const string manifestName = "dataset.jsonl";
        const string recipeName = "training-recipe.sh";
        if (string.IsNullOrWhiteSpace(destinationDirectory))
            throw new ArgumentException("Choose a dataset destination.", nameof(destinationDirectory));

        string destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectory));
        string destinationName = Path.GetFileName(destination);
        string? destinationParent = Path.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(destinationName) || string.IsNullOrEmpty(destinationParent))
            throw new ArgumentException("Choose a dataset folder below a parent directory.", nameof(destinationDirectory));
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException($"The dataset destination already exists: {destination}");

        var selected = pairs.ToArray();
        if (selected.Length == 0)
            throw new ArgumentException("Select at least one reviewed video/caption pair.", nameof(pairs));

        var prepared = new List<PreparedPair>(selected.Length);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? sourceDirectory = null;
        foreach (var pair in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pair is null || string.IsNullOrWhiteSpace(pair.MediaPath) ||
                string.IsNullOrWhiteSpace(pair.CaptionPath))
                throw new ArgumentException("Every selected pair needs a video and caption path.", nameof(pairs));
            if (pair.ReviewStatus != DatasetReviewStatus.Reviewed)
                throw new ArgumentException("Only reviewed pairs can be finalized.", nameof(pairs));
            if (!pair.HasVideo)
                throw new ArgumentException("Audio-only exports cannot be finalized as a video dataset.", nameof(pairs));

            string video = Path.GetFullPath(pair.MediaPath);
            string caption = Path.GetFullPath(pair.CaptionPath);
            string videoName = Path.GetFileName(video);
            string captionName = Path.GetFileName(caption);
            string stem = Path.GetFileNameWithoutExtension(videoName);
            string extension = Path.GetExtension(videoName);
            if (string.IsNullOrWhiteSpace(stem) ||
                !(extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
                  extension.Equals(".webm", StringComparison.OrdinalIgnoreCase) ||
                  extension.Equals(".mov", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"The dataset needs an MP4, WebM, or MOV video: {videoName}", nameof(pairs));
            if (!captionName.Equals(stem + ".txt", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"The caption must match the video basename: {videoName}", nameof(pairs));
            if (!names.Add(stem))
                throw new ArgumentException($"Duplicate video basename in dataset: {stem}", nameof(pairs));

            string? videoDirectory = Path.GetDirectoryName(video);
            string? captionDirectory = Path.GetDirectoryName(caption);
            if (videoDirectory is null || captionDirectory is null ||
                !SamePath(videoDirectory, captionDirectory))
                throw new ArgumentException($"The video and caption must be in the same exports folder: {videoName}", nameof(pairs));
            if (sourceDirectory is null) sourceDirectory = videoDirectory;
            else if (!SamePath(sourceDirectory, videoDirectory))
                throw new ArgumentException("All selected pairs must come from one exports folder.", nameof(pairs));
            if (!File.Exists(video) || new FileInfo(video).Length == 0)
                throw new FileNotFoundException("The selected video is missing or empty.", video);
            if (!File.Exists(caption))
                throw new FileNotFoundException("The selected caption is missing.", caption);
            if (pair.ReviewedCaptionHash is { } reviewedHash)
            {
                await using var reviewedFile = File.OpenRead(caption);
                string actualHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(reviewedFile, cancellationToken));
                if (!actualHash.Equals(reviewedHash, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException(
                        $"The caption changed after it was reviewed: {captionName}. Review it again before finalizing.",
                        nameof(pairs));
            }
            string captionText = (await File.ReadAllTextAsync(caption, cancellationToken)).Trim();
            if (captionText.Length == 0)
                throw new ArgumentException($"Review and save a caption for {videoName} before finalizing.", nameof(pairs));

            var media = await MediaProbe.ReadAsync(video, cancellationToken);
            if (!media.HasVideo)
                throw new ArgumentException($"The selected file has no video stream: {videoName}", nameof(pairs));
            if (ProjectMediaPolicyRules.GetRejection(mediaPolicy, media.HasVideo, media.HasAudio) is { } rejection)
                throw new ArgumentException($"{videoName}: {rejection}", nameof(pairs));
            if (mediaPolicy == ProjectMediaPolicy.VideoOnly && media.HasAudio)
                throw new ArgumentException($"{videoName} has audio, but this is a video-only project. Re-export it without audio.",
                    nameof(pairs));
            prepared.Add(new PreparedPair(video, caption, videoName, captionName, captionText,
                pair.ReviewedCaptionHash));
        }

        if (IsSameOrChildOf(destination, sourceDirectory!))
            throw new ArgumentException("The finalized dataset must be outside the exports folder.", nameof(destinationDirectory));

        // A text file can be paired with more than one media extension when older
        // or manually copied exports share a basename. Do not delete that text as
        // part of one selected pair while another working pair still needs it.
        var mediaByStem = Directory.EnumerateFiles(sourceDirectory!)
            .Where(path => MediaExtensions.Contains(Path.GetExtension(path)))
            .GroupBy(path => Path.GetFileNameWithoutExtension(path)!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (var pair in prepared)
        {
            string stem = Path.GetFileNameWithoutExtension(pair.VideoName);
            if (mediaByStem.TryGetValue(stem, out string[]? matchingMedia) &&
                matchingMedia.Any(path => !SamePath(Path.GetFullPath(path), pair.VideoPath)))
                throw new ArgumentException(
                    $"Another media file shares the caption basename {stem}; rename or separate the pairs before finalizing.",
                    nameof(pairs));
        }

        Directory.CreateDirectory(destinationParent);
        string staging = Path.Combine(destinationParent,
            "." + destinationName + ".partial-" + Guid.NewGuid().ToString("N"));
        var publishedHashes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        bool published = false;
        int videosWithoutAudio = 0;
        try
        {
            Directory.CreateDirectory(staging);
            var manifest = new StringBuilder();
            var trainingShapes = new List<LtxTrainingRecipe.VideoShape>(prepared.Count);
            for (int index = 0; index < prepared.Count; index++)
            {
                var pair = prepared[index];
                cancellationToken.ThrowIfCancellationRequested();
                string videoCopy = Path.Combine(staging, pair.VideoName);
                string captionCopy = Path.Combine(staging, pair.CaptionName);
                publishedHashes[pair.VideoPath] = await CopyVerifiedAsync(pair.VideoPath, videoCopy, cancellationToken);
                byte[] captionHash = await CopyVerifiedAsync(pair.CaptionPath, captionCopy, cancellationToken);
                publishedHashes[pair.CaptionPath] = captionHash;
                if (pair.ReviewedCaptionHash is { } reviewedHash &&
                    !Convert.ToHexStringLower(captionHash).Equals(reviewedHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"The caption changed during finalization: {pair.CaptionName}");
                trainingShapes.Add(await LtxTrainingRecipe.InspectAsync(videoCopy, cancellationToken));
                if ((await File.ReadAllTextAsync(captionCopy, cancellationToken)).Trim() != pair.Caption)
                    throw new IOException($"The caption changed during finalization: {pair.CaptionName}");
                manifest.Append(JsonSerializer.Serialize(new { video = pair.VideoName, caption = pair.Caption }));
                manifest.Append('\n');
                progress?.Report(new DatasetFinalizationProgress(index + 1, prepared.Count, pair.VideoName));
            }

            string manifestPath = Path.Combine(staging, manifestName);
            videosWithoutAudio = trainingShapes.Count(shape => !shape.HasAudio);
            await File.WriteAllTextAsync(manifestPath, manifest.ToString(), new UTF8Encoding(false), cancellationToken);
            await ValidateManifestAsync(manifestPath, staging, prepared, cancellationToken);
            string recipePath = Path.Combine(staging, recipeName);
            await File.WriteAllTextAsync(recipePath, LtxTrainingRecipe.Render(trainingShapes, mediaPolicy),
                new UTF8Encoding(false), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
            published = true;
        }
        finally
        {
            if (!published && Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }

        // Publication is already complete. Persist duplicate history before removing
        // any working file; if that fails, leave every source pair available for repair.
        try
        {
            var catalog = new DatasetCatalog(sourceDirectory!);
            await catalog.RecordPublishedBatchHashesAsync(prepared.Select(pair => pair.VideoPath));
        }
        catch (Exception error)
        {
            var allSources = prepared.SelectMany(pair => new[] { pair.VideoPath, pair.CaptionPath }).ToArray();
            return new DatasetFinalizationResult(destination, Path.Combine(destination, manifestName),
                Path.Combine(destination, recipeName),
                prepared.Count, allSources,
                $"The dataset was published, but duplicate history could not be saved: {error.Message} " +
                "Working exports were kept.", videosWithoutAudio);
        }

        // Cancellation or a failed source deletion must never make the published
        // dataset appear to have failed or disappear.
        var retained = new List<string>();
        foreach (var pair in prepared)
        {
            foreach (string path in new[] { pair.VideoPath, pair.CaptionPath })
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    await using (var current = File.OpenRead(path))
                    {
                        byte[] currentHash = await SHA256.HashDataAsync(current);
                        if (!currentHash.AsSpan().SequenceEqual(publishedHashes[path]))
                        {
                            retained.Add(path);
                            continue;
                        }
                    }
                    File.Delete(path);
                }
                catch (Exception) { retained.Add(path); }
            }
        }
        return new DatasetFinalizationResult(destination, Path.Combine(destination, manifestName),
            Path.Combine(destination, recipeName),
            prepared.Count, retained, VideosWithoutAudio: videosWithoutAudio);
    }

    private static async Task<byte[]> CopyVerifiedAsync(string source, string destination,
        CancellationToken cancellationToken)
    {
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                         1024 * 1024, FileOptions.SequentialScan))
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                         1024 * 1024, FileOptions.SequentialScan))
            await input.CopyToAsync(output, cancellationToken);

        await using var original = File.OpenRead(source);
        await using var copy = File.OpenRead(destination);
        byte[] originalHash = await SHA256.HashDataAsync(original, cancellationToken);
        byte[] copyHash = await SHA256.HashDataAsync(copy, cancellationToken);
        if (!originalHash.AsSpan().SequenceEqual(copyHash))
            throw new IOException($"The copied file does not match its source: {Path.GetFileName(source)}");
        return originalHash;
    }

    private static async Task ValidateManifestAsync(string manifestPath, string staging,
        IReadOnlyList<PreparedPair> pairs, CancellationToken cancellationToken)
    {
        string[] rows = (await File.ReadAllTextAsync(manifestPath, cancellationToken))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (rows.Length != pairs.Count)
            throw new IOException("The dataset manifest is incomplete.");
        for (int i = 0; i < rows.Length; i++)
        {
            using var document = JsonDocument.Parse(rows[i]);
            var root = document.RootElement;
            if (root.GetProperty("video").GetString() != pairs[i].VideoName ||
                root.GetProperty("caption").GetString() != pairs[i].Caption ||
                !File.Exists(Path.Combine(staging, pairs[i].VideoName)) ||
                !File.Exists(Path.Combine(staging, pairs[i].CaptionName)))
                throw new IOException("The dataset manifest does not match the copied pairs.");
        }
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(left), Path.TrimEndingDirectorySeparator(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsSameOrChildOf(string path, string parent)
    {
        string relative = Path.GetRelativePath(parent, path);
        return relative == "." ||
               (!Path.IsPathRooted(relative) && relative != ".." &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }
}
