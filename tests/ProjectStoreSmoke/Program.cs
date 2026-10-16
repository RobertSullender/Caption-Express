using System.Text.Json;
using System.Text.Json.Nodes;
using CaptionExpress.Dataset;

string temp = Path.Combine(Path.GetTempPath(), "caption-project-store-" + Guid.NewGuid().ToString("N"));
string exports = Path.Combine(temp, "exports");
Directory.CreateDirectory(exports);
try
{
    string legacy = Path.Combine(exports, "Vid-0001.txt");
    await File.WriteAllTextAsync(legacy, "Existing loose export remains untouched.\n");
    var store = new ExportProjectStore(exports);
    Require((await store.ListAsync()).Count == 0 && await store.GetActiveNameAsync() is null,
        "an empty exports root has no implicit project");

    JsonElement ltxSettings;
    using (var document = JsonDocument.Parse("""
        {"id":"ltx-23","codecId":"h264-mp4","fps":[24],"frameStep":8,"buckets":[{"width":1024,"height":1024}]}
        """))
        ltxSettings = document.RootElement.Clone();
    ExportProject ltx = await store.CreateAsync("LTX 2.3", "ltx-23", ltxSettings);
    Require(ltx.DirectoryPath == Path.Combine(exports, "LTX 2.3") &&
            ltx.MediaPolicy == ProjectMediaPolicy.JointVideoAudio &&
            ltx.EffectiveSettings.GetProperty("frameStep").GetInt32() == 8 &&
            File.Exists(Path.Combine(ltx.DirectoryPath, ".caption-express", "project.json")),
        "a project is a direct child of exports with a durable effective-settings snapshot");
    Require(await File.ReadAllTextAsync(legacy) == "Existing loose export remains untouched.\n",
        "project creation does not change legacy loose files");
    Require((await new DatasetCatalog(ltx.DirectoryPath).ListPairsAsync()).Count == 0,
        "opening the review tab can inspect an empty project before custom settings are saved");

    JsonElement updatedLtx = JsonSerializer.SerializeToElement(new
    {
        id = "ltx-23", codecId = "hevc-mp4", fps = new[] { 24 }, frameStep = 8,
        buckets = new[] { new { width = 1344, height = 768 } }
    });
    ExportProject updated = await store.UpdateEffectiveSettingsAsync(ltx.Name, updatedLtx);
    Require(updated.PresetId == ltx.PresetId &&
            (await new ExportProjectStore(exports).OpenAsync(ltx.Name))?.EffectiveSettings
                .GetProperty("codecId").GetString() == "hevc-mp4",
        "custom effective settings persist across restart before the first export");
    await File.WriteAllTextAsync(Path.Combine(ltx.DirectoryPath, "Vid-0001.txt"), "Draft\n");
    await ExpectFailureAsync<InvalidOperationException>(() =>
        store.UpdateEffectiveSettingsAsync(ltx.Name, ltxSettings),
        "a project with exports cannot change its profile silently");

    JsonElement racingSettings = JsonSerializer.SerializeToElement(new
    {
        id = "ltx-23", codecId = "h264-mp4", fps = new[] { 24 }, frameStep = 8,
        buckets = new[] { new { width = 1536, height = 640 } }
    });
    ExportProject racing = await store.CreateAsync("LTX 2.3 - Racing", "ltx-23", racingSettings,
        ProjectMediaPolicy.VideoOnly);
    Require((await store.ListAsync()).Select(project => project.Name)
            .SequenceEqual(["LTX 2.3", "LTX 2.3 - Racing"]) &&
            (await store.OpenAsync("ltx 2.3 - racing"))?.DirectoryPath == racing.DirectoryPath &&
            (await new ExportProjectStore(exports).OpenAsync(racing.Name))?.MediaPolicy ==
            ProjectMediaPolicy.VideoOnly,
        "list and open find named projects and their media policy case-insensitively");
    ExportProject racingUpdated = await store.UpdateEffectiveSettingsAsync(racing.Name,
        JsonSerializer.SerializeToElement(new
        {
            id = "ltx-23", codecId = "h264-mp4", fps = new[] { 24, 30 }, frameStep = 8,
            buckets = new[] { new { width = 1536, height = 640 } }
        }));
    Require(racingUpdated.MediaPolicy == ProjectMediaPolicy.VideoOnly &&
            (await new ExportProjectStore(exports).OpenAsync(racing.Name))?.MediaPolicy ==
            ProjectMediaPolicy.VideoOnly,
        "editing an empty project's frame settings cannot change its media type");
    JsonElement fixedMp4 = JsonSerializer.SerializeToElement(new
    {
        DefaultVideoFormat = new { Id = "h264-mp4", Name = "MP4", Extension = ".mp4" }
    });
    JsonElement changedWebM = JsonSerializer.SerializeToElement(new
    {
        DefaultVideoFormat = new { Id = "vp9-webm", Name = "WebM", Extension = ".webm" }
    });
    ExportProject fixedFormat = await store.CreateAsync("Fixed Format", "ltx-23", fixedMp4);
    await ExpectFailureAsync<InvalidOperationException>(() =>
        store.UpdateEffectiveSettingsAsync(fixedFormat.Name, changedWebM),
        "an empty project cannot switch to another video format");
    Require((await store.OpenAsync(fixedFormat.Name))?.EffectiveSettings
                .GetProperty("DefaultVideoFormat").GetProperty("Id").GetString() == "h264-mp4",
        "the original project format remains saved after a rejected change");
    await store.SetActiveNameAsync(racing.Name);
    Require(await new ExportProjectStore(exports).GetActiveNameAsync() == racing.Name,
        "last-active project survives store restart");

    await ExpectFailureAsync<IOException>(() => store.CreateAsync("ltx 2.3 - racing", "ltx-23", racingSettings),
        "case-insensitive duplicate name");
    foreach (string invalid in new[] { "../Escape", ".hidden", "bad/name", " trailing", "trailing ",
                 "LTX.", "CON", "", new string('x', 65) })
        await ExpectFailureAsync<ArgumentException>(() => store.CreateAsync(invalid, "ltx-23", ltxSettings),
            $"invalid name {invalid}");
    await ExpectFailureAsync<ArgumentException>(() => store.CreateAsync("No Settings", "ltx-23",
        JsonSerializer.SerializeToElement(new { })), "empty effective settings");

    string unrelated = Path.Combine(exports, "Unregistered");
    Directory.CreateDirectory(unrelated);
    Require((await store.ListAsync()).Count == 3, "unregistered folders are not projects");
    await ExpectFailureAsync<InvalidDataException>(() => store.OpenAsync("Unregistered"),
        "unregistered folder cannot be opened as a project");

    string outside = Path.Combine(temp, "outside");
    Directory.CreateDirectory(outside);
    string symlink = Path.Combine(exports, "Linked Project");
    Directory.CreateSymbolicLink(symlink, outside);
    await ExpectFailureAsync<InvalidDataException>(() => store.OpenAsync("Linked Project"),
        "project symlink cannot escape exports root");
    await ExpectFailureAsync<IOException>(() => store.CreateAsync("Linked Project", "ltx-23", ltxSettings),
        "symlink name cannot be reused");

    string duplicateName = "Concurrent";
    var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
    {
        try { await store.CreateAsync(duplicateName, "ltx-23", ltxSettings); return true; }
        catch (IOException) { return false; }
    }));
    Require(results.Count(value => value) == 1 && (await store.ListAsync()).Count == 4,
        "concurrent creation publishes exactly one uniquely named project");

    ExportProject oldProject = await store.CreateAsync("Old Project", "ltx-23", ltxSettings);
    string oldMetadata = Path.Combine(oldProject.DirectoryPath, ".caption-express", "project.json");
    JsonNode oldJson = JsonNode.Parse(await File.ReadAllTextAsync(oldMetadata))!;
    oldJson.AsObject().Remove("mediaPolicy");
    await File.WriteAllTextAsync(oldMetadata, oldJson.ToJsonString());
    Require((await store.OpenAsync(oldProject.Name))?.MediaPolicy == ProjectMediaPolicy.LegacyMixed,
        "projects created before media-policy selection retain their legacy mixed behavior");

    ExportProject finished = await store.CreateAsync("Finished", "ltx-23", ltxSettings);
    await store.SetActiveNameAsync(finished.Name);
    string finishedState = Path.Combine(finished.DirectoryPath, ".caption-express");
    await File.WriteAllTextAsync(Path.Combine(finishedState, "catalog.json"), "{}");
    await File.WriteAllTextAsync(Path.Combine(finishedState, "next-id"), "76");
    await File.WriteAllTextAsync(Path.Combine(finishedState, "catalog.lock"), "");
    await File.WriteAllTextAsync(Path.Combine(finishedState, "sequence.lock"), "");
    string pending = Path.Combine(finished.DirectoryPath, "Vid-0076.mp4");
    await File.WriteAllTextAsync(pending, "working media");
    Require(!(await store.TryRetireEmptyProjectAsync(finished.Name)).Retired && File.Exists(pending),
        "a working pair prevents project retirement");
    File.Delete(pending);
    string userNote = Path.Combine(finished.DirectoryPath, "notes.txt");
    await File.WriteAllTextAsync(userNote, "keep this");
    Require(!(await store.TryRetireEmptyProjectAsync(finished.Name)).Retired && File.Exists(userNote),
        "an unrelated project file is never deleted by retirement");
    File.Delete(userNote);
    string unknownState = Path.Combine(finishedState, "custom-state.txt");
    await File.WriteAllTextAsync(unknownState, "keep this too");
    Require(!(await store.TryRetireEmptyProjectAsync(finished.Name)).Retired && File.Exists(unknownState),
        "unknown hidden state is never deleted by retirement");
    File.Delete(unknownState);
    Require((await store.TryRetireEmptyProjectAsync(finished.Name)).Retired &&
            !Directory.Exists(finished.DirectoryPath) && await store.GetActiveNameAsync() is null &&
            File.Exists(legacy),
        "an empty finished project and its active pointer are removed without touching loose exports");

    await store.SetActiveNameAsync(ltx.Name);
    Require((await store.TryRetireEmptyProjectAsync(oldProject.Name)).Retired &&
            await store.GetActiveNameAsync() == ltx.Name,
        "retiring a different project preserves the active selection");

    string rootState = Path.Combine(exports, ".caption-express");
    ExportProject interrupted = await store.CreateAsync("Interrupted", "ltx-23", ltxSettings);
    await store.SetActiveNameAsync(interrupted.Name);
    string interruptedRetiring = Path.Combine(rootState, "retiring-" + Guid.NewGuid().ToString("N"));
    Directory.Move(interrupted.DirectoryPath, interruptedRetiring);
    Require((await store.ListAsync()).All(project => project.Name != interrupted.Name) &&
            !Directory.Exists(interruptedRetiring) && !Directory.Exists(interrupted.DirectoryPath) &&
            await store.GetActiveNameAsync() is null,
        "startup completes an interrupted cleanup and clears its stale active pointer");

    ExportProject partiallyRemoved = await store.CreateAsync("Partially Removed", "ltx-23", ltxSettings);
    string partialRetiring = Path.Combine(rootState, "retiring-" + Guid.NewGuid().ToString("N"));
    Directory.Move(partiallyRemoved.DirectoryPath, partialRetiring);
    File.Delete(Path.Combine(partialRetiring, ".caption-express", "project.json"));
    await store.ListAsync();
    Require(!Directory.Exists(partialRetiring),
        "startup completes cleanup when app metadata was already deleted before shutdown");

    ExportProject interruptedWithUserFile = await store.CreateAsync("Restore User File", "ltx-23", ltxSettings);
    string userFile = Path.Combine(interruptedWithUserFile.DirectoryPath, "notes.txt");
    await File.WriteAllTextAsync(userFile, "Keep this file");
    string restoreRetiring = Path.Combine(rootState, "retiring-" + Guid.NewGuid().ToString("N"));
    Directory.Move(interruptedWithUserFile.DirectoryPath, restoreRetiring);
    Require((await store.ListAsync()).Any(project => project.Name == interruptedWithUserFile.Name) &&
            File.Exists(userFile) && !Directory.Exists(restoreRetiring),
        "startup restores an interrupted project when a user file needs inspection");

    ExportProject orphaned = await store.CreateAsync("Orphaned Retirement", "ltx-23", ltxSettings);
    string orphanRetiring = Path.Combine(rootState, "retiring-" + Guid.NewGuid().ToString("N"));
    Directory.Move(orphaned.DirectoryPath, orphanRetiring);
    File.Delete(Path.Combine(orphanRetiring, ".caption-express", "project.json"));
    string orphanNote = Path.Combine(orphanRetiring, "notes.txt");
    await File.WriteAllTextAsync(orphanNote, "Keep this too");
    Require((await store.ListAsync()).Any(project => project.Name == interruptedWithUserFile.Name) &&
            File.Exists(orphanNote) && store.RecoveryWarning?.Contains(orphanRetiring) == true,
        "unidentified recovery files stay quarantined without blocking other projects");

    await store.SetActiveNameAsync(ltx.Name);
    Directory.Delete(ltx.DirectoryPath, recursive: true);
    Require(await store.GetActiveNameAsync() is null,
        "deleted active project does not silently select another project");
    Console.WriteLine("Project store smoke test passed.");
}
finally
{
    Directory.Delete(temp, recursive: true);
}

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception("Project store check failed: " + message);
}

static async Task ExpectFailureAsync<T>(Func<Task> action, string message) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception("Project store unexpectedly accepted " + message);
}
