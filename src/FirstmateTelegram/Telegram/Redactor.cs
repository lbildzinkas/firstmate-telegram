using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FirstmateTelegram.Telegram;

/// <summary>
/// The deny list (spec 6): projects that must never be named on Telegram. Structured rows whose project or
/// repository matches are replaced whole with "a private project", and free text is filtered on a best-effort
/// basis: each entry, and any GitHub URL whose repository matches one, case-insensitively and on whole words.
/// An empty list passes everything through unchanged, and that is how it ships.
/// </summary>
public sealed partial class Redactor
{
    public const string Mask = "a private project";

    readonly List<string> _entries;
    readonly IReadOnlyList<Regex> _entryPatterns;

    public static Redactor None { get; } = new([]);

    public Redactor(IReadOnlyList<string> denyList)
    {
        _entries = [.. denyList.Select(Normalize).Where(entry => entry.Length > 0)];
        // Whole words only, case-insensitively; a deny entry may name a project directory or an owner/repository.
        _entryPatterns = _entries
            .Select(entry => new Regex($"(?<![\\w/@-]){Regex.Escape(entry)}(?![\\w/-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1)))
            .ToList();
    }

    public bool IsActive => _entries.Count > 0;

    /// <summary>Whether a structured row for this project directory or repository may show nothing but the mask.</summary>
    public bool IsDenied(string? projectOrRepository) =>
        projectOrRepository is not null && _entries.Contains(Normalize(projectOrRepository), StringComparer.OrdinalIgnoreCase);

    /// <summary>Best-effort free-text filtering: entries and GitHub URLs naming a denied repository become the mask.</summary>
    public string Apply(string text)
    {
        if (!IsActive)
            return text;

        var result = new StringBuilder(text);
        foreach (var url in GitHubUrl().Matches(text).ToList())
        {
            var repository = url.Groups["repo"].Value;
            if (IsDenied(repository) || IsDenied(url.Groups["owner"].Value + "/" + repository))
                result.Replace(url.Value, Mask, url.Index, url.Length);
        }

        // The URLs already masked may have carried an entry as their path; filter the entries on what is left.
        var remainder = result.ToString();
        foreach (var pattern in _entryPatterns)
            remainder = pattern.Replace(remainder, Mask);
        return remainder;
    }

    static string Normalize(string value) => value.Trim().TrimEnd('/');

    [GeneratedRegex(@"https://github\.com/(?<owner>[^/\s]+)/(?<repo>[^/\s?#]+)(?:/[^\s]*)?", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubUrl();
}
