using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FileRenamer.Services;

public static class OnlineBookSearchService
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static async Task<string> SearchBookOnlineAsync(string extractedText, string originalName)
    {
        try
        {
            // 1. Try to find ISBN first (highest precision)
            var isbnMatch = Regex.Match(extractedText, @"\bISBN(?:-1[03])?[:\s]*(?=[-0-9X]{10,17})([\d\-]{9,17}X?)\b", RegexOptions.IgnoreCase);
            string? isbn = null;
            if (isbnMatch.Success)
            {
                isbn = isbnMatch.Groups[1].Value.Replace("-", "").Replace(" ", "").Trim();
            }

            string url;
            if (!string.IsNullOrEmpty(isbn))
            {
                url = $"https://www.googleapis.com/books/v1/volumes?q=isbn:{isbn}";
            }
            else
            {
                // 2. Clean query search
                // Use first non-trivial line from the extracted text or original name
                var lines = extractedText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(l => l.Length > 5 && !l.Contains("http") && !l.Contains("@"))
                    .Take(5)
                    .ToList();

                string query = originalName;
                if (lines.Count > 0)
                {
                    query = lines[0];
                }

                // Clean query string
                query = Regex.Replace(query, @"\b[a-fA-F0-9]{32}\b", "");
                query = Regex.Replace(query, @"\.(pdf|epub|mobi|docx|doc|txt)$", "", RegexOptions.IgnoreCase);
                query = query.Replace("_", " ").Trim();

                if (query.Length > 100)
                {
                    query = query[..100];
                }

                url = $"https://www.googleapis.com/books/v1/volumes?q={Uri.EscapeDataString(query)}&maxResults=1";
            }

            var sb = new System.Text.StringBuilder();

            // 1. Google Books / ISBN search
            string googleBooksResult = "";
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");

                var response = await _http.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);

                    if (doc.RootElement.TryGetProperty("items", out var items) && items.GetArrayLength() > 0)
                    {
                        var volumeInfo = items[0].GetProperty("volumeInfo");
                        
                        string title = volumeInfo.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                        
                        var authorsList = new List<string>();
                        if (volumeInfo.TryGetProperty("authors", out var auths))
                        {
                            foreach (var a in auths.EnumerateArray())
                            {
                                authorsList.Add(a.GetString() ?? "");
                            }
                        }
                        string authors = string.Join(", ", authorsList);
                        
                        string publishedDate = volumeInfo.TryGetProperty("publishedDate", out var pd) ? pd.GetString() ?? "" : "";
                        string year = !string.IsNullOrEmpty(publishedDate) && publishedDate.Length >= 4 ? publishedDate[..4] : "";
                        
                        string publisher = volumeInfo.TryGetProperty("publisher", out var pub) ? pub.GetString() ?? "" : "";

                        var bookSb = new System.Text.StringBuilder();
                        bookSb.AppendLine("Verified Book Metadata (from Google Books / ISBN search):");
                        if (!string.IsNullOrEmpty(title)) bookSb.AppendLine($"Title: {title}");
                        if (!string.IsNullOrEmpty(authors)) bookSb.AppendLine($"Author(s): {authors}");
                        if (!string.IsNullOrEmpty(year)) bookSb.AppendLine($"Year: {year}");
                        if (!string.IsNullOrEmpty(publisher)) bookSb.AppendLine($"Publisher: {publisher}");
                        
                        googleBooksResult = bookSb.ToString().Trim();
                    }
                }
            }
            catch
            {
                // Ignore single search failure
            }

            if (!string.IsNullOrWhiteSpace(googleBooksResult))
            {
                sb.AppendLine(googleBooksResult);
            }

            // 2. Perform general Web Search (Google / Web Search)
            string webSearchQuery = originalName;
            var cleanLines = extractedText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => l.Length > 5 && !l.Contains("http") && !l.Contains("@"))
                .Take(2)
                .ToList();
            if (cleanLines.Count > 0)
            {
                webSearchQuery = cleanLines[0];
            }

            string webSearchResult = await PerformWebSearchAsync(webSearchQuery);
            if (!string.IsNullOrWhiteSpace(webSearchResult))
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.AppendLine(webSearchResult.Trim());
            }

            return sb.ToString();
        }
        catch
        {
            // Ignore search network exceptions, just return empty to proceed gracefully
        }

        return "";
    }

    private static async Task<string> PerformWebSearchAsync(string query)
    {
        try
        {
            // Clean query: strip extensions, illegal characters, hashes
            query = Regex.Replace(query, @"\b[a-fA-F0-9]{32}\b", "");
            query = Regex.Replace(query, @"\.(pdf|epub|mobi|docx|doc|txt)$", "", RegexOptions.IgnoreCase);
            query = query.Replace("_", " ").Trim();
            if (query.Length > 100) query = query[..100];
            if (string.IsNullOrWhiteSpace(query)) return "";

            var request = new HttpRequestMessage(HttpMethod.Get, $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}");
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");

            using var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return "";

            var html = await response.Content.ReadAsStringAsync();

            var titleMatches = Regex.Matches(html, @"<a class=""result__a""[^>]*>(.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            var snippetMatches = Regex.Matches(html, @"<a class=""result__snippet""[^>]*>(.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

            var results = new List<string>();
            for (int i = 0; i < Math.Min(3, titleMatches.Count); i++)
            {
                string rawTitle = Regex.Replace(titleMatches[i].Groups[1].Value, @"<[^>]+>", "").Trim();
                rawTitle = System.Net.WebUtility.HtmlDecode(rawTitle);

                string rawSnippet = "";
                if (i < snippetMatches.Count)
                {
                    rawSnippet = Regex.Replace(snippetMatches[i].Groups[1].Value, @"<[^>]+>", "").Trim();
                    rawSnippet = System.Net.WebUtility.HtmlDecode(rawSnippet);
                }

                if (!string.IsNullOrWhiteSpace(rawTitle))
                {
                    results.Add($"- {rawTitle}" + (!string.IsNullOrWhiteSpace(rawSnippet) ? $": {rawSnippet}" : ""));
                }
            }

            if (results.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Verified Web Search Results (from Google/Web search for '{query}'):");
                foreach (var r in results)
                {
                    sb.AppendLine(r);
                }
                return sb.ToString();
            }
        }
        catch
        {
            // Graceful fallback
        }

        return "";
    }
}
