using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CaptionExpress.Dataset;

public enum DatasetReviewStatus
{
    Pending,
    Reviewed,
    Excluded
}

public sealed record DatasetPair(string MediaPath, string? CaptionPath,
    DatasetReviewStatus ReviewStatus, bool HasVideo, string? SourceHash,
    string? ReviewedCaptionHash = null);

/// <summary>
/// Keeps a durable record of completed source files and review decisions. Media and captions
/// remain ordinary files in the export directory; the catalog lives in a hidden subdirectory.
/// </summary>
public sealed class DatasetCatalog
{
    private const int CurrentVersion = 1;
    // This is the historical format of batch MP4 exports before format IDs were recorded.
    private const string LegacyBatchMp4FormatId = "h264-mp4";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".webm", ".mov", ".mkv", ".avi"
    };
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".wav", ".mp3", ".m4a", ".ogg", ".opus"
    };

    private readonly string _exportsDirectory;
    private readonly string _catalogDirectory;
    private readonly string _catalogPath;
    private readonly SemaphoreSlim _gate;

    public DatasetCatalog(string exportsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportsDirectory);
        _exportsDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(exportsDirectory));
        _catalogDirectory = Path.Combine(_exportsDirectory, ".caption-express");
        _catalogPath = Path.Combine(_catalogDirectory, "catalog.json");
        _gate = Gates.GetOrAdd(_catalogPath, _ => new SemaphoreSlim(1, 1));
    }

    /// <summary>
    /// Hashes the complete source stream. Size and modification time are checked before and
    /// after reading so a file modified during hashing cannot enter the duplicate history.
    /// </summary>
    public async Task<string> ComputeSourceHashAsync(string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = new FileInfo(path);
        file.Refresh();
        long initialLength = file.Length;
        DateTime initialWrite = file.LastWriteTimeUtc;
        await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
            FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] digest = await SHA256.HashDataAsync(stream, cancellationToken);
        file.Refresh();
        if (file.Length != initialLength || file.LastWriteTimeUtc != initialWrite ||
            stream.Position != initialLength)
            throw new IOException($"Source changed while being hashed: {file.FullName}");
        return Convert.ToHexStringLower(digest);
    }

    /// <summary>
    /// Reports a source previously completed or excluded by a folder run. One-clip exports
    /// may be custom trims and never mark the entire source as processed.
    /// </summary>
    public async Task<bool> ContainsSourceHashAsync(string hash,
        CancellationToken cancellationToken = default)
    {
        ValidateHash(hash);
        using (await AcquireCatalogLockAsync(cancellationToken))
        {
            CatalogState catalog = await ReadAsync(cancellationToken);
            if (catalog.SourceHashes!.Contains(hash, StringComparer.OrdinalIgnoreCase)) return true;
            foreach ((string key, CatalogExport entry) in catalog.Exports!)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!entry.BatchExport ||
                    !string.Equals(entry.SourceHash, hash, StringComparison.OrdinalIgnoreCase)) continue;
                string mediaPath = Path.Combine(_exportsDirectory, key);
                if (!File.Exists(mediaPath)) continue;
                if (entry.ReviewStatus == DatasetReviewStatus.Excluded ||
                    await HasNonEmptyCaptionAsync(mediaPath, cancellationToken)) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Finds a prior batch export that needs captioning, allowing a resumed batch to use its
    /// media rather than rendering another copy. A one-clip export stays separate.
    /// </summary>
    public async Task<DatasetPair?> FindIncompleteExportByHashAsync(string hash,
        CancellationToken cancellationToken = default, string? videoFormatId = null)
    {
        ValidateHash(hash);
        using (await AcquireCatalogLockAsync(cancellationToken))
        {
            CatalogState catalog = await ReadAsync(cancellationToken);
            foreach ((string key, CatalogExport entry) in catalog.Exports!)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(entry.SourceHash, hash, StringComparison.OrdinalIgnoreCase) ||
                    entry.ReviewStatus != DatasetReviewStatus.Pending || !entry.BatchExport) continue;
                string mediaPath = Path.Combine(_exportsDirectory, key);
                if (!File.Exists(mediaPath) || await HasNonEmptyCaptionAsync(mediaPath, cancellationToken)) continue;
                if (videoFormatId is not null && VideoExtensions.Contains(Path.GetExtension(mediaPath)))
                {
                    // Older batch exports were always H.264 MP4. A matching extension alone
                    // cannot distinguish H.264, HEVC, and AV1 exports made by newer builds.
                    string? recordedFormat = entry.VideoFormatId;
                    if (recordedFormat is null && Path.GetExtension(mediaPath)
                            .Equals(".mp4", StringComparison.OrdinalIgnoreCase))
                        recordedFormat = LegacyBatchMp4FormatId;
                    if (!string.Equals(recordedFormat, videoFormatId, StringComparison.Ordinal)) continue;
                }
                return new DatasetPair(mediaPath, File.Exists(Path.ChangeExtension(mediaPath, ".txt"))
                    ? Path.ChangeExtension(mediaPath, ".txt") : null, entry.ReviewStatus,
                    VideoExtensions.Contains(Path.GetExtension(mediaPath)), entry.SourceHash);
            }
            return null;
        }
    }

    /// <summary>
    /// Records an actual export. A batch export may be resumed after an interrupted caption job;
    /// an unfinished one-clip export is never commandeered by a later folder run.
    /// </summary>
    public async Task RecordExportAsync(string mediaPath, string sourceHash,
        CancellationToken cancellationToken = default, bool batchExport = false,
        string? videoFormatId = null)
    {
        ValidateHash(sourceHash);
        string key = ExportKey(mediaPath);
        if (!File.Exists(mediaPath)) throw new FileNotFoundException("Exported media does not exist.", mediaPath);
        using (await AcquireCatalogLockAsync(cancellationToken))
        {
            CatalogState catalog = await ReadAsync(cancellationToken);
            catalog.Exports!.TryGetValue(key, out CatalogExport? existing);
            bool sameSource = string.Equals(existing?.SourceHash, sourceHash,
                StringComparison.OrdinalIgnoreCase);
            catalog.Exports[key] = new CatalogExport
            {
                SourceHash = sourceHash.ToLowerInvariant(),
                ReviewStatus = sameSource ? existing!.ReviewStatus : DatasetReviewStatus.Pending,
                ReviewedCaptionHash = sameSource ? existing!.ReviewedCaptionHash : null,
                BatchExport = batchExport || (sameSource && existing?.BatchExport == true),
                VideoFormatId = VideoExtensions.Contains(Path.GetExtension(mediaPath))
                    ? videoFormatId ?? (sameSource ? existing?.VideoFormatId : null) : null
            };
            await WriteAsync(catalog, cancellationToken);
        }
    }

    public async Task SetReviewStatusAsync(string mediaPath, DatasetReviewStatus status,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        string key = ExportKey(mediaPath);
        if (!File.Exists(mediaPath)) throw new FileNotFoundException("Exported media does not exist.", mediaPath);
        string? reviewedHash = null;
        if (status == DatasetReviewStatus.Reviewed)
        {
            reviewedHash = await SavedCaptionHashAsync(mediaPath, cancellationToken);
            if (reviewedHash is null)
                throw new InvalidOperationException("Save a nonempty caption before marking an export reviewed.");
        }
        using (await AcquireCatalogLockAsync(cancellationToken))
        {
            CatalogState catalog = await ReadAsync(cancellationToken);
            catalog.Exports!.TryGetValue(key, out CatalogExport? existing);
            catalog.Exports[key] = new CatalogExport
            {
                SourceHash = existing?.SourceHash,
                ReviewStatus = status,
                ReviewedCaptionHash = reviewedHash,
                BatchExport = existing?.BatchExport == true,
                VideoFormatId = existing?.VideoFormatId
            };
            await WriteAsync(catalog, cancellationToken);
        }
    }

    /// <summary>Finds current media and matching text files, including pre-catalog exports.</summary>
    public async Task<IReadOnlyList<DatasetPair>> ListPairsAsync(CancellationToken cancellationToken = default)
    {
        using (await AcquireCatalogLockAsync(cancellationToken))
        {
            CatalogState catalog = await ReadAsync(cancellationToken);
            if (!Directory.Exists(_exportsDirectory)) return [];
            var pairs = new List<DatasetPair>();
            foreach (string path in Directory.EnumerateFiles(_exportsDirectory)
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string extension = Path.GetExtension(path);
                bool hasVideo = VideoExtensions.Contains(extension);
                if (!hasVideo && !AudioExtensions.Contains(extension)) continue;
                string? captionPath = Path.ChangeExtension(path, ".txt");
                if (!File.Exists(captionPath)) captionPath = null;
                string key = Path.GetFileName(path);
                catalog.Exports!.TryGetValue(key, out CatalogExport? entry);
                DatasetReviewStatus status = entry?.ReviewStatus ?? DatasetReviewStatus.Pending;
                string? reviewedHash = null;
                if (status == DatasetReviewStatus.Reviewed)
                {
                    string? actualHash = await SavedCaptionHashAsync(path, cancellationToken);
                    if (entry?.ReviewedCaptionHash is { } expectedHash &&
                        string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                        reviewedHash = expectedHash;
                    else status = DatasetReviewStatus.Pending;
                }
                pairs.Add(new DatasetPair(path, captionPath, status, hasVideo, entry?.SourceHash, reviewedHash));
            }
            return pairs;
        }
    }

    /// <summary>
    /// Records the batch sources included in an already published dataset before export files
    /// are removed. This is idempotent and leaves review status and active export entries intact.
    /// </summary>
    public async Task RecordPublishedBatchHashesAsync(IEnumerable<string> mediaPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mediaPaths);
        using (await AcquireCatalogLockAsync(cancellationToken))
        {
            CatalogState catalog = await ReadAsync(cancellationToken);
            bool changed = false;
            foreach (string mediaPath in mediaPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string key = ExportKey(mediaPath);
                if (!catalog.Exports!.TryGetValue(key, out CatalogExport? entry) ||
                    !entry.BatchExport || entry.SourceHash is not { } hash || !IsValidHash(hash) ||
                    catalog.SourceHashes!.Contains(hash, StringComparer.OrdinalIgnoreCase)) continue;
                catalog.SourceHashes!.Add(hash.ToLowerInvariant());
                changed = true;
            }
            if (changed) await WriteAsync(catalog, cancellationToken);
        }
    }

    /// <summary>
    /// Removes one active export record after a successful finalization. Batch source digests
    /// enter permanent history; one-clip trims do not block future full-source folder runs.
    /// </summary>
    public async Task RemoveExportAsync(string mediaPath, CancellationToken cancellationToken = default)
    {
        string key = ExportKey(mediaPath);
        using (await AcquireCatalogLockAsync(cancellationToken))
        {
            CatalogState catalog = await ReadAsync(cancellationToken);
            if (catalog.Exports!.Remove(key, out CatalogExport? entry))
            {
                if (entry.BatchExport && entry.SourceHash is { } hash &&
                    !catalog.SourceHashes!.Contains(hash, StringComparer.OrdinalIgnoreCase))
                    catalog.SourceHashes!.Add(hash);
                await WriteAsync(catalog, cancellationToken);
            }
        }
    }

    private async Task<CatalogLock> AcquireCatalogLockAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_catalogDirectory);
            string lockPath = Path.Combine(_catalogDirectory, "catalog.lock");
            var wait = Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileStream? stream = null;
                try
                {
                    stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                        FileShare.None, 1, FileOptions.None);
                    return new CatalogLock(_gate, stream);
                }
                catch (IOException)
                {
                    stream?.Dispose();
                    if (wait.Elapsed >= TimeSpan.FromSeconds(30)) throw;
                    await Task.Delay(50, cancellationToken);
                }
            }
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    private sealed class CatalogLock(SemaphoreSlim gate, FileStream stream) : IDisposable
    {
        public void Dispose()
        {
            try { stream.Dispose(); }
            finally { gate.Release(); }
        }
    }

    private string ExportKey(string mediaPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);
        string fullPath = Path.GetFullPath(mediaPath);
        string parent = Path.GetDirectoryName(fullPath) ?? "";
        if (!string.Equals(parent, _exportsDirectory,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Exported media must be directly inside the selected export folder.", nameof(mediaPath));
        string extension = Path.GetExtension(fullPath);
        if (!VideoExtensions.Contains(extension) && !AudioExtensions.Contains(extension))
            throw new ArgumentException("This file type is not a supported exported media file.", nameof(mediaPath));
        return Path.GetFileName(fullPath);
    }

    private async Task<CatalogState> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_catalogPath)) return new CatalogState();
        await using var stream = new FileStream(_catalogPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 8192, FileOptions.Asynchronous);
        CatalogState catalog = await JsonSerializer.DeserializeAsync<CatalogState>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The dataset catalog is empty.");
        if (catalog.Version != CurrentVersion)
            throw new InvalidDataException($"Unsupported dataset catalog version {catalog.Version}.");
        catalog.SourceHashes ??= [];
        catalog.Exports ??= new Dictionary<string, CatalogExport>(StringComparer.Ordinal);
        return catalog;
    }

    private async Task WriteAsync(CatalogState catalog, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_catalogDirectory);
        string partial = Path.Combine(_catalogDirectory, ".catalog-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, catalog, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partial, _catalogPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    private static void ValidateHash(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        if (!IsValidHash(hash))
            throw new ArgumentException("A SHA-256 digest must contain exactly 64 hex digits.", nameof(hash));
    }

    private static bool IsValidHash(string hash) => hash.Length == 64 && hash.All(Uri.IsHexDigit);

    private static async Task<bool> HasNonEmptyCaptionAsync(string mediaPath, CancellationToken cancellationToken)
    {
        return await SavedCaptionHashAsync(mediaPath, cancellationToken) is not null;
    }

    private static async Task<string?> SavedCaptionHashAsync(string mediaPath, CancellationToken cancellationToken)
    {
        string captionPath = Path.ChangeExtension(mediaPath, ".txt");
        if (!File.Exists(captionPath)) return null;
        byte[] bytes;
        try { bytes = await File.ReadAllBytesAsync(captionPath, cancellationToken); }
        catch (FileNotFoundException) { return null; }
        if (string.IsNullOrWhiteSpace(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'))) return null;
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed class CatalogState
    {
        public int Version { get; set; } = CurrentVersion;
        public List<string>? SourceHashes { get; set; } = [];
        public Dictionary<string, CatalogExport>? Exports { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class CatalogExport
    {
        public string? SourceHash { get; set; }
        public DatasetReviewStatus ReviewStatus { get; set; } = DatasetReviewStatus.Pending;
        public string? ReviewedCaptionHash { get; set; }
        public bool BatchExport { get; set; }
        public string? VideoFormatId { get; set; }
    }
}
