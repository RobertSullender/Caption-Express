using System.Diagnostics;
using System.Globalization;

namespace CaptionExpress.Media;

// The counter lives with the exports so single-file and folder runs share one sequence.
// Publishing is serialized across app instances; an interrupted transcode never takes an ID.
internal static class NumberedExportPaths
{
    private const string StateDirectoryName = ".caption-express";
    private const string CounterFileName = "next-id";
    private const string LockFileName = "sequence.lock";

    public static string TemporaryPath(string directory, string extension)
    {
        string stateDirectory = Path.Combine(directory, StateDirectoryName);
        Directory.CreateDirectory(stateDirectory);
        return Path.Combine(stateDirectory, $"export-{Guid.NewGuid():N}.partial{extension}");
    }

    public static async Task<string> PublishAsync(string directory, string temporaryPath, string extension,
        CancellationToken cancellationToken)
    {
        string stateDirectory = Path.Combine(directory, StateDirectoryName);
        Directory.CreateDirectory(stateDirectory);
        await using var sequenceLock = await AcquireLockAsync(
            Path.Combine(stateDirectory, LockFileName), cancellationToken);

        string counterPath = Path.Combine(stateDirectory, CounterFileName);
        long next = Math.Max(ReadNextId(counterPath), checked(MaxExistingId(directory) + 1));
        if (next == long.MaxValue) throw new IOException("The export filename sequence is exhausted.");
        string stem = "Vid-" + next.ToString("D4", CultureInfo.InvariantCulture);
        string finalPath = Path.Combine(directory, stem + extension);

        // Commit the next ID before publishing. Even if the app closes just after this
        // write, later exports cannot reuse an ID whose files were subsequently removed.
        string counterTemporary = Path.Combine(stateDirectory, $"next-id-{Guid.NewGuid():N}.tmp");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await File.WriteAllTextAsync(counterTemporary,
                (next + 1).ToString(CultureInfo.InvariantCulture) + Environment.NewLine, cancellationToken);
            File.Move(counterTemporary, counterPath, overwrite: true);
            cancellationToken.ThrowIfCancellationRequested();
            // The non-overwriting move is the final guard against a file created by
            // another program between the directory scan and publication.
            File.Move(temporaryPath, finalPath);
            return finalPath;
        }
        finally
        {
            if (File.Exists(counterTemporary)) File.Delete(counterTemporary);
        }
    }

    private static long ReadNextId(string counterPath)
    {
        if (!File.Exists(counterPath)) return 1;
        string saved = File.ReadAllText(counterPath).Trim();
        if (!long.TryParse(saved, NumberStyles.None, CultureInfo.InvariantCulture, out long next) || next < 1)
            throw new IOException($"The export sequence file is invalid: {counterPath}");
        return next;
    }

    private static long MaxExistingId(string directory)
    {
        long max = 0;
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            string name = Path.GetFileName(entry);
            if (!name.StartsWith("Vid-", StringComparison.OrdinalIgnoreCase)) continue;
            ReadOnlySpan<char> remainder = name.AsSpan(4);
            int dot = remainder.IndexOf('.');
            ReadOnlySpan<char> digits = dot < 0 ? remainder : remainder[..dot];
            if (long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long id) &&
                id > max)
                max = id;
        }
        return max;
    }

    private static async Task<FileStream> AcquireLockAsync(string path, CancellationToken cancellationToken)
    {
        var wait = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (wait.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(50, cancellationToken);
            }
        }
    }
}
