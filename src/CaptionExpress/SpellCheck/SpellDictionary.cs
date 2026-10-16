using System.Text;
using System.Text.RegularExpressions;
using WeCantSpell.Hunspell;

namespace CaptionExpress.SpellCheck;

internal readonly record struct SpellingIssue(int Start, int Length, string Word);

/// <summary>Offline English spelling with a per-user list of accepted words.</summary>
internal sealed class SpellDictionary
{
    private static readonly Regex WordPattern = new(@"[\p{L}\p{M}]+(?:['’\-][\p{L}\p{M}]+)*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PromptTagPattern = new(@"\[\[\s*[\p{L}_]+\s*:",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PlaceholderPattern = new(@"\{[\p{L}_]+\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UrlPattern = new(@"\b(?:https?://|www\.)\S+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> AppTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "Gemma", "GGUF", "mmproj", "LTX", "Qwen", "ComfyUI", "VRAM", "JSONL", "fps", "bpm",
        "captioning", "timecode", "lo-fi", "bass-line"
    };

    private readonly WordList? _wordList;
    private readonly HashSet<string> _personalWords = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _personalLock = new();
    private readonly string _personalPath;

    internal static SpellDictionary Instance { get; } = new();
    internal event Action? PersonalWordsChanged;
    internal string? Error { get; }
    internal bool IsAvailable => _wordList is not null;

    internal SpellDictionary(string? dictionaryPath = null, string? configHome = null)
    {
        configHome ??= Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome) || !Path.IsPathFullyQualified(configHome))
            configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        _personalPath = Path.Combine(configHome, "Caption-Express", "dictionary-en_US.txt");

        try
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, "dictionaries", "en_US.dic");
            var system = "/usr/share/hunspell/en_US.dic";
            var path = dictionaryPath ?? (File.Exists(bundled) && File.Exists(Path.ChangeExtension(bundled, ".aff"))
                ? bundled : system);
            if (!File.Exists(path) || !File.Exists(Path.ChangeExtension(path, ".aff")))
                throw new FileNotFoundException("English dictionary not found. Reinstall Caption-Express or install Ubuntu's hunspell-en-us package.");
            _wordList = WordList.CreateFromFiles(path);
        }
        catch (Exception ex)
        {
            Error = $"Spell checking unavailable: {ex.Message}";
        }

        try
        {
            if (File.Exists(_personalPath))
            {
                foreach (var word in File.ReadLines(_personalPath, Encoding.UTF8))
                {
                    var trimmed = word.Trim();
                    if (IsSingleWord(trimmed)) _personalWords.Add(trimmed);
                }
            }
        }
        catch (Exception ex)
        {
            Error ??= $"Personal spell dictionary unavailable: {ex.Message}";
        }
    }

    internal IReadOnlyList<SpellingIssue> FindIssues(string text)
    {
        if (_wordList is null || string.IsNullOrEmpty(text)) return [];
        var excluded = PromptTagPattern.Matches(text).Cast<Match>()
            .Concat(PlaceholderPattern.Matches(text).Cast<Match>())
            .Concat(UrlPattern.Matches(text).Cast<Match>())
            .Select(m => (m.Index, End: m.Index + m.Length)).ToArray();
        var result = new List<SpellingIssue>();
        foreach (Match match in WordPattern.Matches(text))
        {
            if (match.Length < 2 || excluded.Any(range => match.Index < range.End && match.Index + match.Length > range.Index))
                continue;
            // A token touching a digit or underscore is part of a technical identifier or time label.
            if (match.Index > 0 && IsIdentifierCharacter(text[match.Index - 1]) ||
                match.Index + match.Length < text.Length && IsIdentifierCharacter(text[match.Index + match.Length]))
                continue;
            if (!IsCorrect(match.Value)) result.Add(new SpellingIssue(match.Index, match.Length, match.Value));
        }
        return result;
    }

    internal bool IsCorrect(string word)
    {
        if (_wordList is null || AppTerms.Contains(word)) return true;
        lock (_personalLock)
            if (_personalWords.Contains(word)) return true;
        return _wordList.Check(Normalize(word));
    }

    internal IReadOnlyList<string> Suggest(string word)
    {
        if (_wordList is null) return [];
        return _wordList.Suggest(Normalize(word)).Take(6).ToArray();
    }

    internal bool AddWord(string word, out string? error)
    {
        error = null;
        word = word.Trim();
        if (!IsSingleWord(word))
        {
            error = "Choose one word to add to the dictionary.";
            return false;
        }
        lock (_personalLock)
        {
            if (_personalWords.Contains(word)) return true;
            _personalWords.Add(word);
            var temporary = _personalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_personalPath)!);
                File.WriteAllLines(temporary, _personalWords.Order(StringComparer.OrdinalIgnoreCase), new UTF8Encoding(false));
                File.Move(temporary, _personalPath, true);
            }
            catch (Exception ex)
            {
                _personalWords.Remove(word);
                try { File.Delete(temporary); } catch { /* best effort */ }
                error = $"Could not save your spell dictionary: {ex.Message}";
                return false;
            }
        }
        PersonalWordsChanged?.Invoke();
        return true;
    }

    private static bool IsSingleWord(string word) =>
        word.Length > 1 && WordPattern.Match(word) is { Success: true, Index: 0 } match && match.Length == word.Length;

    private static bool IsIdentifierCharacter(char value) => char.IsLetterOrDigit(value) || value == '_';

    private static string Normalize(string word) => word.Replace('’', '\'');
}
