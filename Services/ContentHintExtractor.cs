using System.Text.RegularExpressions;

namespace FileRenamer.Services;

public static class ContentHintExtractor
{
    // Dates: 2024-03-15, 15/03/2024, March 15 2024, etc.
    private static readonly Regex DateRegex = new(
        @"\b(\d{1,2}[\/\-.]\d{1,2}[\/\-.]\d{2,4}|\d{4}[\/\-.]\d{1,2}[\/\-.]\d{1,2}|(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\s+\d{1,2},?\s+\d{4})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Invoice / order IDs
    private static readonly Regex IdRegex = new(
        @"\b(Invoice|Order|Ref(?:erence)?|Receipt|Ticket|Case|PO|No|Num(?:ber)?)\s*#?\s*[:\-]?\s*([A-Z0-9\-]{3,20})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "By Author Name" or "Author: Name" near the start
    private static readonly Regex AuthorRegex = new(
        @"\b(?:by|author[:\s]+|written\s+by\s+)([A-Z][a-z]+(?: [A-Z][a-z]+){1,3})",
        RegexOptions.Compiled);

    // ISBN-10 or ISBN-13
    private static readonly Regex IsbnRegex = new(
        @"\bISBN(?:-1[03])?[:\s]*(?=[-0-9X]{10,17})([\d\-]{9,17}X?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Edition markers
    private static readonly Regex EditionRegex = new(
        @"\b(\d+(?:st|nd|rd|th)|[Ff]irst|[Ss]econd|[Tt]hird|[Ff]ourth|[Ff]ifth)\s+[Ee]dition\b",
        RegexOptions.Compiled);

    // Publisher names after "Published by" / "Publisher:"
    private static readonly Regex PublisherRegex = new(
        @"\b(?:Publisher|Published\s+by)\s*:?\s*([A-Z][A-Za-z &,]+?)(?:\.|,|\n|$)",
        RegexOptions.Compiled);

    // Year alone (1900–2099) – only when other context is present
    private static readonly Regex YearRegex = new(
        @"\b(19[5-9]\d|20[0-2]\d)\b",
        RegexOptions.Compiled);

    // Chapter / section headings that look like a title
    private static readonly Regex ChapterRegex = new(
        @"^(?:Chapter|CHAPTER|Section|SECTION)\s+\d+[\s:\-]+(.+)$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public static string BuildHints(string extractedText)
    {
        if (string.IsNullOrWhiteSpace(extractedText)) return "";

        var lines = extractedText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Length > 3)
            .ToList();

        // First non-trivial line is often the title
        string probableTitle = lines.FirstOrDefault(l => l.Length > 5) ?? "";

        // Look for title in first 20 lines – prefer ALL-CAPS or title-case lines
        var shortCandidate = lines.Take(20)
            .FirstOrDefault(l => l.Length is > 8 and < 120
                && (l == l.ToUpper() || char.IsUpper(l[0]))
                && !l.Contains("http") && !l.Contains('@'));
        if (!string.IsNullOrEmpty(shortCandidate))
            probableTitle = shortCandidate;

        var searchText = extractedText.Length > 8000 ? extractedText[..8000] : extractedText;

        var dates      = DateRegex.Matches(searchText).Select(m => m.Value).Distinct().Take(2).ToList();
        var ids        = IdRegex.Matches(searchText).Select(m => $"{m.Groups[1].Value} {m.Groups[2].Value}").Distinct().Take(2).ToList();
        var authors    = AuthorRegex.Matches(searchText).Select(m => m.Groups[1].Value.Trim()).Distinct().Take(2).ToList();
        var isbns      = IsbnRegex.Matches(searchText).Select(m => m.Groups[1].Value.Trim()).Distinct().Take(1).ToList();
        var editions   = EditionRegex.Matches(searchText).Select(m => m.Value.Trim()).Distinct().Take(1).ToList();
        var publishers = PublisherRegex.Matches(searchText).Select(m => m.Groups[1].Value.Trim()).Distinct().Take(1).ToList();
        var years      = YearRegex.Matches(searchText).Select(m => m.Value).Distinct().OrderByDescending(y => y).Take(2).ToList();

        var hints = new List<string>();
        if (!string.IsNullOrWhiteSpace(probableTitle))  hints.Add($"Probable title   : \"{probableTitle}\"");
        if (authors.Count > 0)                          hints.Add($"Author(s)        : {string.Join(", ", authors)}");
        if (editions.Count > 0)                         hints.Add($"Edition          : {string.Join(", ", editions)}");
        if (isbns.Count > 0)                            hints.Add($"ISBN             : {string.Join(", ", isbns)}");
        if (publishers.Count > 0)                       hints.Add($"Publisher        : {string.Join(", ", publishers)}");
        if (dates.Count > 0)                            hints.Add($"Date(s) found    : {string.Join(", ", dates)}");
        if (years.Count > 0)                            hints.Add($"Year(s) found    : {string.Join(", ", years)}");
        if (ids.Count > 0)                              hints.Add($"ID/reference     : {string.Join(", ", ids)}");

        return string.Join("\n", hints);
    }
}
