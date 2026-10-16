using CaptionExpress.SpellCheck;

var root = Path.Combine(Path.GetTempPath(), "caption-spelling-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var dictionaryPath = Path.Combine(AppContext.BaseDirectory, "dictionaries", "en_US.dic");
    Require(File.Exists(dictionaryPath), "bundled English dictionary exists");
    var checker = new SpellDictionary(dictionaryPath, root);
    Require(checker.IsAvailable && checker.Error is null, "bundled English dictionary loads offline");
    var sample = "[[later_audio:]] {previous_caption} 00:05 LTX2.3 https://example.org/Gemma " +
                 "The girll crawls through a tunnnel.";
    var issues = checker.FindIssues(sample);
    Require(issues.Any(i => i.Word == "girll") && issues.Any(i => i.Word == "tunnnel"),
        "misspellings in prose are marked");
    Require(issues.All(i => i.Word is not ("later" or "audio" or "previous" or "caption" or "LTX" or "Gemma")),
        "prompt tags, placeholders, timestamps, and URLs are ignored");
    Require(checker.Suggest("girll").Count > 0, "correction suggestions are offered");
    Require(checker.AddWord("girll", out var error) && error is null, "personal word is saved");
    Require(!checker.FindIssues(sample).Any(i => i.Word == "girll"), "added word clears its underline");
    var reloaded = new SpellDictionary(dictionaryPath, root);
    Require(!reloaded.FindIssues(sample).Any(i => i.Word == "girll"),
        "personal dictionary survives app restart");
    Require(!checker.AddWord("two words", out _), "only a single word may be added");
    Console.WriteLine("SpellCheckSmoke passed.");
}
finally
{
    Directory.Delete(root, true);
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
