using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CaptionExpress.Dataset;

/// <summary>Which source streams a project accepts for its training dataset.</summary>
public enum ProjectMediaPolicy
{
    // Metadata created before this setting existed has the default enum value.
    LegacyMixed = 0,
    JointVideoAudio = 1,
    VideoOnly = 2
}

public static class ProjectMediaPolicyRules
{
    /// <returns>A reason to skip the file, or null when it is eligible.</returns>
    public static string? GetRejection(ProjectMediaPolicy policy, bool hasVideo, bool hasAudio) =>
        policy switch
        {
            ProjectMediaPolicy.LegacyMixed when !hasVideo && !hasAudio =>
                "The file has no video or audio stream.",
            ProjectMediaPolicy.LegacyMixed => null,
            ProjectMediaPolicy.JointVideoAudio when !hasVideo =>
                "This project requires a video stream.",
            ProjectMediaPolicy.JointVideoAudio when !hasAudio =>
                "This project requires an embedded audio stream.",
            ProjectMediaPolicy.JointVideoAudio => null,
            ProjectMediaPolicy.VideoOnly when !hasVideo =>
                "This project requires a video stream.",
            ProjectMediaPolicy.VideoOnly => null,
            _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown project media policy.")
        };
}

/// <summary>
/// A named working dataset. Every export, catalog, and filename counter lives directly
/// inside DirectoryPath; the app's exports root contains only project directories.
/// </summary>
public sealed record ExportProject(string Name, string DirectoryPath, string PresetId,
    JsonElement EffectiveSettings, int Version, DateTimeOffset CreatedUtc,
    ProjectMediaPolicy MediaPolicy)
{
    public override string ToString() => Name;
}

public sealed record ProjectRetirementResult(bool Retired, int RemainingEntries, string? Warning = null);

/// <summary>
/// Creates and opens user-named project folders directly below an exports root. Preset
/// settings are snapshotted in each project so a later app update cannot silently alter
/// the meaning of an existing project's exports.
/// </summary>
public sealed class ExportProjectStore
{
    private const int CurrentVersion = 1;
    private const int MaximumNameLength = 64;
    private const string StateDirectoryName = ".caption-express";
    private const string ProjectFileName = "project.json";
    private const string ActiveFileName = "active-project.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _exportsRoot;
    private readonly string _rootStateDirectory;

    public ExportProjectStore(string exportsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportsRoot);
        _exportsRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(exportsRoot));
        _rootStateDirectory = Path.Combine(_exportsRoot, StateDirectoryName);
    }

    public string ExportsRoot => _exportsRoot;
    public string? RecoveryWarning { get; private set; }

    public async Task<IReadOnlyList<ExportProject>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_exportsRoot)) return [];
        await using var gate = await AcquireLockAsync(cancellationToken);
        var projects = new List<ExportProject>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in Directory.EnumerateDirectories(_exportsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = Path.GetFileName(directory);
            if (name.Equals(StateDirectoryName, StringComparison.OrdinalIgnoreCase) ||
                IsLink(directory)) continue;
            string metadataPath = ProjectMetadataPath(directory);
            if (!File.Exists(metadataPath)) continue;
            ExportProject project = await ReadProjectAsync(directory, cancellationToken);
            if (!names.Add(project.Name.Normalize(NormalizationForm.FormC)))
                throw new InvalidDataException($"Multiple project folders have the name '{project.Name}'.");
            projects.Add(project);
        }
        return projects.OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<ExportProject?> OpenAsync(string name, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        if (!Directory.Exists(_exportsRoot)) return null;
        await using var gate = await AcquireLockAsync(cancellationToken);
        return await OpenCoreAsync(name, cancellationToken);
    }

    public Task<ExportProject> CreateAsync(string name, string presetId,
        JsonElement effectiveSettings, CancellationToken cancellationToken = default) =>
        CreateAsync(name, presetId, effectiveSettings, ProjectMediaPolicy.JointVideoAudio, cancellationToken);

    public async Task<ExportProject> CreateAsync(string name, string presetId,
        JsonElement effectiveSettings, ProjectMediaPolicy mediaPolicy,
        CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        ValidatePreset(presetId, effectiveSettings);
        ValidateMediaPolicy(mediaPolicy);
        cancellationToken.ThrowIfCancellationRequested();
        await using var gate = await AcquireLockAsync(cancellationToken);
        foreach (string entry in Directory.EnumerateFileSystemEntries(_exportsRoot))
        {
            if (NamesEqual(Path.GetFileName(entry), name))
                throw new IOException($"A project or folder named '{name}' already exists in exports.");
        }

        string directory = ProjectPath(name);
        Directory.CreateDirectory(directory);
        try
        {
            string stateDirectory = Path.Combine(directory, StateDirectoryName);
            Directory.CreateDirectory(stateDirectory);
            var metadata = new ProjectFile
            {
                Version = CurrentVersion,
                Name = name,
                PresetId = presetId,
                MediaPolicy = mediaPolicy,
                EffectiveSettings = effectiveSettings.Clone(),
                CreatedUtc = DateTimeOffset.UtcNow
            };
            await WriteNewAsync(ProjectMetadataPath(directory), metadata, cancellationToken);
            return ToProject(directory, metadata);
        }
        catch
        {
            // Only an empty, newly-created project directory is safe to undo. Keep
            // any unexpected user file instead of deleting a directory recursively.
            string stateDirectory = Path.Combine(directory, StateDirectoryName);
            if (Directory.Exists(stateDirectory) && !Directory.EnumerateFileSystemEntries(stateDirectory).Any())
                Directory.Delete(stateDirectory);
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
            throw;
        }
    }

    /// <summary>
    /// Saves edited preset settings while a project is still empty. Once it has produced
    /// an export, changing its settings would mix different training profiles under one
    /// name, so the user must create another project instead.
    /// </summary>
    public async Task<ExportProject> UpdateEffectiveSettingsAsync(string name,
        JsonElement effectiveSettings, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        await using var gate = await AcquireLockAsync(cancellationToken);
        ExportProject existing = await OpenCoreAsync(name, cancellationToken)
            ?? throw new DirectoryNotFoundException($"Project '{name}' does not exist.");
        ValidatePreset(existing.PresetId, effectiveSettings);
        if (!string.Equals(ReadDefaultVideoFormatId(existing.EffectiveSettings),
                ReadDefaultVideoFormatId(effectiveSettings), StringComparison.Ordinal))
            throw new InvalidOperationException(
                "A project's video format is fixed when it is created. Create a new project to use another format.");
        if (Directory.EnumerateFileSystemEntries(existing.DirectoryPath)
                .Any(path => !Path.GetFileName(path).Equals(StateDirectoryName, StringComparison.Ordinal)) ||
            File.Exists(Path.Combine(existing.DirectoryPath, StateDirectoryName, "next-id")) ||
            File.Exists(Path.Combine(existing.DirectoryPath, StateDirectoryName, "catalog.json")))
            throw new InvalidOperationException(
                "This project has exported files or catalog history. Create a new project for different settings.");
        var metadata = new ProjectFile
        {
            Version = CurrentVersion,
            Name = existing.Name,
            PresetId = existing.PresetId,
            MediaPolicy = existing.MediaPolicy,
            EffectiveSettings = effectiveSettings.Clone(),
            CreatedUtc = existing.CreatedUtc
        };
        await WriteReplaceAsync(ProjectMetadataPath(existing.DirectoryPath), metadata, cancellationToken);
        return ToProject(existing.DirectoryPath, metadata);
    }

    public async Task<string?> GetActiveNameAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_exportsRoot)) return null;
        await using var gate = await AcquireLockAsync(cancellationToken);
        string path = Path.Combine(_rootStateDirectory, ActiveFileName);
        if (!File.Exists(path)) return null;
        RejectLink(path);
        ActiveProjectFile? active = JsonSerializer.Deserialize<ActiveProjectFile>(
            await File.ReadAllTextAsync(path, cancellationToken), JsonOptions);
        if (active?.Version != CurrentVersion || active.Name is null)
            throw new InvalidDataException($"Invalid active project metadata: {path}");
        ValidateName(active.Name);
        if (await OpenCoreAsync(active.Name, cancellationToken) is { } project) return project.Name;
        // A shutdown can occur after a finished project is removed but before
        // its active pointer is cleared. The pointer is app-owned and stale.
        File.Delete(path);
        return null;
    }

    public async Task SetActiveNameAsync(string name, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        await using var gate = await AcquireLockAsync(cancellationToken);
        ExportProject project = await OpenCoreAsync(name, cancellationToken)
            ?? throw new DirectoryNotFoundException($"Project '{name}' does not exist.");
        string path = Path.Combine(_rootStateDirectory, ActiveFileName);
        await WriteReplaceAsync(path,
            new ActiveProjectFile { Version = CurrentVersion, Name = project.Name }, cancellationToken);
    }

    /// <summary>
    /// Removes a finished project only if no working exports or unrelated files remain.
    /// Call this after a finalized dataset has been published and its source pairs removed.
    /// Retirement discards the project's filename counter and duplicate history.
    /// </summary>
    public async Task<ProjectRetirementResult> TryRetireEmptyProjectAsync(string name,
        CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        await using var gate = await AcquireLockAsync(cancellationToken);
        ExportProject project = await OpenCoreAsync(name, cancellationToken)
            ?? throw new DirectoryNotFoundException($"Project '{name}' does not exist.");
        string stateDirectory = Path.Combine(project.DirectoryPath, StateDirectoryName);
        int remaining = CountNonAppOwnedEntries(project.DirectoryPath, stateDirectory);
        if (remaining != 0) return new ProjectRetirementResult(false, remaining);

        // Move the verified project out of the working list before deleting its
        // metadata. The root lock prevents another project-store operation from
        // claiming the same name during this step.
        string retiringDirectory = Path.Combine(_rootStateDirectory,
            "retiring-" + Guid.NewGuid().ToString("N"));
        Directory.Move(project.DirectoryPath, retiringDirectory);
        string retiringState = Path.Combine(retiringDirectory, StateDirectoryName);
        var stateBackup = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            remaining = CountNonAppOwnedEntries(retiringDirectory, retiringState);
            if (remaining != 0)
            {
                Directory.Move(retiringDirectory, project.DirectoryPath);
                return new ProjectRetirementResult(false, remaining);
            }
            foreach (string path in Directory.EnumerateFiles(retiringState))
                stateBackup.Add(Path.GetFileName(path), await File.ReadAllBytesAsync(path, cancellationToken));
            foreach (string path in Directory.EnumerateFiles(retiringState)
                         .Where(path => Path.GetFileName(path) != ProjectFileName))
                File.Delete(path);
            File.Delete(ProjectMetadataPath(retiringDirectory));
            Directory.Delete(retiringState);
            Directory.Delete(retiringDirectory);
        }
        catch
        {
            // Restore app-owned state if a late filesystem error interrupts cleanup.
            // Never overwrite a file that appeared after the first inspection.
            if (Directory.Exists(retiringDirectory) && !Directory.Exists(project.DirectoryPath))
            {
                Directory.CreateDirectory(retiringState);
                foreach ((string fileName, byte[] bytes) in stateBackup)
                {
                    string path = Path.Combine(retiringState, fileName);
                    if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
                }
                Directory.Move(retiringDirectory, project.DirectoryPath);
            }
            throw;
        }

        string activePath = Path.Combine(_rootStateDirectory, ActiveFileName);
        string? warning = null;
        try
        {
            if (File.Exists(activePath))
            {
                RejectLink(activePath);
                ActiveProjectFile? active = JsonSerializer.Deserialize<ActiveProjectFile>(
                    await File.ReadAllTextAsync(activePath, cancellationToken), JsonOptions);
                if (active?.Version == CurrentVersion && active.Name is not null &&
                    NamesEqual(active.Name, project.Name))
                    File.Delete(activePath);
            }
        }
        catch (Exception error)
        {
            // The project folder is already gone. Report this as a warning rather
            // than making the caller recreate it while refreshing the review list.
            warning = "Could not clear the active-project pointer: " + error.Message;
        }
        return new ProjectRetirementResult(true, 0, warning);
    }

    private async Task<ExportProject?> OpenCoreAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string[] matches = Directory.EnumerateFileSystemEntries(_exportsRoot)
            .Where(entry => NamesEqual(Path.GetFileName(entry), name)).ToArray();
        if (matches.Length == 0) return null;
        if (matches.Length > 1)
            throw new InvalidDataException($"Multiple files or folders use the project name '{name}'.");
        string entry = matches[0];
        if (!Directory.Exists(entry))
            throw new InvalidDataException($"The project name '{name}' is used by a non-project file.");
        RejectLink(entry);
        if (!File.Exists(ProjectMetadataPath(entry)))
            throw new InvalidDataException($"The folder '{name}' is missing its project metadata.");
        return await ReadProjectAsync(entry, cancellationToken);
    }

    private async Task<ExportProject> ReadProjectAsync(string directory, CancellationToken cancellationToken)
    {
        string folderName = Path.GetFileName(directory);
        ValidateName(folderName);
        if (!string.Equals(ProjectPath(folderName), Path.GetFullPath(directory), PathComparison()))
            throw new InvalidDataException("The project folder is outside the exports root.");
        RejectLink(directory);
        string stateDirectory = Path.Combine(directory, StateDirectoryName);
        RejectLink(stateDirectory);
        string path = ProjectMetadataPath(directory);
        RejectLink(path);
        ProjectFile? metadata = JsonSerializer.Deserialize<ProjectFile>(
            await File.ReadAllTextAsync(path, cancellationToken), JsonOptions);
        if (metadata?.Version != CurrentVersion || metadata.Name is null || metadata.PresetId is null ||
            !string.Equals(metadata.Name, folderName, StringComparison.Ordinal))
            throw new InvalidDataException($"Invalid project metadata: {path}");
        ValidatePreset(metadata.PresetId, metadata.EffectiveSettings);
        ValidateMediaPolicy(metadata.MediaPolicy);
        return ToProject(directory, metadata);
    }

    private static ExportProject ToProject(string directory, ProjectFile metadata) =>
        new(metadata.Name!, Path.GetFullPath(directory), metadata.PresetId!,
            metadata.EffectiveSettings.Clone(), metadata.Version, metadata.CreatedUtc,
            metadata.MediaPolicy);

    private string ProjectPath(string name)
    {
        string fullPath = Path.GetFullPath(Path.Combine(_exportsRoot, name));
        if (!string.Equals(Path.GetDirectoryName(fullPath), _exportsRoot, PathComparison()))
            throw new ArgumentException("A project name must be one folder directly inside exports.", nameof(name));
        return fullPath;
    }

    private static string ProjectMetadataPath(string directory) =>
        Path.Combine(directory, StateDirectoryName, ProjectFileName);

    private static int CountNonAppOwnedEntries(string projectDirectory, string stateDirectory)
    {
        int remaining = 0;
        foreach (string entry in Directory.EnumerateFileSystemEntries(projectDirectory))
        {
            if (!string.Equals(entry, stateDirectory, PathComparison()) || IsLink(entry) ||
                !Directory.Exists(entry)) remaining++;
        }
        if (!Directory.Exists(stateDirectory) || IsLink(stateDirectory)) return remaining + 1;
        foreach (string entry in Directory.EnumerateFileSystemEntries(stateDirectory))
        {
            string fileName = Path.GetFileName(entry);
            if (IsLink(entry) || !File.Exists(entry) || fileName is not
                (ProjectFileName or "catalog.json" or "catalog.lock" or "next-id" or "sequence.lock"))
                remaining++;
        }
        return remaining;
    }

    /// <summary>
    /// Complete an interrupted retirement before any project-store operation.
    /// A project with unexpected files is restored for inspection instead of
    /// being silently deleted. Only the state files we own can be removed.
    /// </summary>
    private async Task RecoverInterruptedRetirementsCoreAsync(CancellationToken cancellationToken)
    {
        foreach (string retiringDirectory in Directory.EnumerateDirectories(_rootStateDirectory, "retiring-*"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string suffix = Path.GetFileName(retiringDirectory)["retiring-".Length..];
            if (!Guid.TryParseExact(suffix, "N", out _)) continue;
            if (IsLink(retiringDirectory))
            {
                RecoveryWarning = $"An interrupted project cleanup is a symbolic link: {retiringDirectory}. " +
                    "It was kept for manual inspection.";
                Trace.TraceWarning(RecoveryWarning);
                continue;
            }
            string stateDirectory = Path.Combine(retiringDirectory, StateDirectoryName);
            if (Directory.Exists(stateDirectory))
            {
                if (IsLink(stateDirectory))
                {
                    RecoveryWarning = $"An interrupted project cleanup contains a symbolic link: {retiringDirectory}. " +
                        "It was kept for manual inspection.";
                    Trace.TraceWarning(RecoveryWarning);
                    continue;
                }
                if (CountNonAppOwnedEntries(retiringDirectory, stateDirectory) == 0)
                {
                    foreach (string path in Directory.EnumerateFiles(stateDirectory)) File.Delete(path);
                    Directory.Delete(stateDirectory);
                    Directory.Delete(retiringDirectory);
                    continue;
                }
            }
            else if (!Directory.EnumerateFileSystemEntries(retiringDirectory).Any())
            {
                Directory.Delete(retiringDirectory);
                continue;
            }

            // A file appeared while retirement was in progress. Preserve it by
            // returning the entire folder to the project list when possible.
            string metadataPath = ProjectMetadataPath(retiringDirectory);
            if (!File.Exists(metadataPath))
            {
                RecoveryWarning = $"An interrupted project cleanup contains files we cannot identify: {retiringDirectory}. " +
                    "The files were kept for manual inspection.";
                Trace.TraceWarning(RecoveryWarning);
                continue;
            }
            if (IsLink(metadataPath))
            {
                RecoveryWarning = $"An interrupted project cleanup has linked metadata: {retiringDirectory}. " +
                    "It was kept for manual inspection.";
                Trace.TraceWarning(RecoveryWarning);
                continue;
            }
            ProjectFile? metadata;
            try
            {
                metadata = JsonSerializer.Deserialize<ProjectFile>(
                    await File.ReadAllTextAsync(metadataPath, cancellationToken), JsonOptions);
                if (metadata?.Version != CurrentVersion || metadata.Name is null)
                    throw new InvalidDataException("Project metadata is incomplete.");
                ValidateName(metadata.Name);
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException)
            {
                RecoveryWarning = $"An interrupted project cleanup has invalid metadata: {retiringDirectory}. " +
                    "The files were kept for manual inspection.";
                Trace.TraceWarning($"{RecoveryWarning} {error.Message}");
                continue;
            }
            string destination = ProjectPath(metadata.Name);
            if (Directory.Exists(destination) || File.Exists(destination))
            {
                RecoveryWarning = $"An interrupted project cleanup could not be restored because its name is in use: " +
                    $"{retiringDirectory}. The files were kept for manual inspection.";
                Trace.TraceWarning(RecoveryWarning);
                continue;
            }
            Directory.Move(retiringDirectory, destination);
        }
    }

    private async Task<FileStream> AcquireLockAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_rootStateDirectory);
        RejectLink(_rootStateDirectory);
        string path = Path.Combine(_rootStateDirectory, "projects.lock");
        var wait = Stopwatch.StartNew();
        FileStream gate;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                gate = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                break;
            }
            catch (IOException) when (wait.Elapsed < TimeSpan.FromSeconds(15))
            { await Task.Delay(50, cancellationToken); }
        }
        try
        {
            await RecoverInterruptedRetirementsCoreAsync(cancellationToken);
            return gate;
        }
        catch
        {
            await gate.DisposeAsync();
            throw;
        }
    }

    private static async Task WriteNewAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, JsonOptions),
                new UTF8Encoding(false), cancellationToken);
            File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task WriteReplaceAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, JsonOptions),
                new UTF8Encoding(false), cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > MaximumNameLength || name != name.Trim() ||
            name.StartsWith('.') || name.EndsWith('.') || name is "." or ".." ||
            name.Equals(StateDirectoryName, StringComparison.OrdinalIgnoreCase) ||
            name.Any(character => !(char.IsLetterOrDigit(character) ||
                                    character is ' ' or '-' or '_' or '.' or '(' or ')')))
            throw new ArgumentException("Use a project name of 1–64 letters, numbers, spaces, hyphens, underscores, periods, or parentheses; avoid leading/trailing spaces and periods.", nameof(name));
        string baseName = name.Split('.')[0];
        if (baseName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (baseName.Length == 4 && (baseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                                      baseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
             baseName[3] is >= '1' and <= '9'))
            throw new ArgumentException("Choose a different project name; this name is reserved by the operating system.", nameof(name));
    }

    private static void ValidatePreset(string presetId, JsonElement settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presetId);
        if (presetId.Length > 128 || presetId != presetId.Trim() ||
            presetId.Any(char.IsControl) || settings.ValueKind != JsonValueKind.Object ||
            !settings.EnumerateObject().Any())
            throw new ArgumentException("A project needs a valid preset ID and nonempty effective-settings snapshot.");
    }

    private static void ValidateMediaPolicy(ProjectMediaPolicy policy)
    {
        if (!Enum.IsDefined(policy))
            throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown project media policy.");
    }

    private static string? ReadDefaultVideoFormatId(JsonElement settings)
    {
        foreach (JsonProperty property in settings.EnumerateObject())
        {
            if (!property.Name.Equals("DefaultVideoFormat", StringComparison.OrdinalIgnoreCase)) continue;
            if (property.Value.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The project video format is invalid.");
            foreach (JsonProperty formatProperty in property.Value.EnumerateObject())
            {
                if (!formatProperty.Name.Equals("Id", StringComparison.OrdinalIgnoreCase)) continue;
                if (formatProperty.Value.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(formatProperty.Value.GetString()))
                    throw new InvalidDataException("The project video format ID is invalid.");
                return formatProperty.Value.GetString();
            }
            throw new InvalidDataException("The project video format ID is missing.");
        }
        return null;
    }

    private static bool NamesEqual(string left, string right) =>
        string.Equals(left.Normalize(NormalizationForm.FormC), right.Normalize(NormalizationForm.FormC),
            StringComparison.OrdinalIgnoreCase);

    private static StringComparison PathComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool IsLink(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static void RejectLink(string path)
    {
        if (IsLink(path)) throw new InvalidDataException($"A project path cannot be a symbolic link: {path}");
    }

    private sealed class ProjectFile
    {
        public int Version { get; set; }
        public string? Name { get; set; }
        public string? PresetId { get; set; }
        [System.Text.Json.Serialization.JsonConverter(
            typeof(System.Text.Json.Serialization.JsonStringEnumConverter<ProjectMediaPolicy>))]
        public ProjectMediaPolicy MediaPolicy { get; set; }
        public JsonElement EffectiveSettings { get; set; }
        public DateTimeOffset CreatedUtc { get; set; }
    }

    private sealed class ActiveProjectFile
    {
        public int Version { get; set; }
        public string? Name { get; set; }
    }
}
