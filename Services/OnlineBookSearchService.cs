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

                // If query is too long, truncate it to avoid API errors
                if (query.Length > 100)
                {
                    query = query[..100];
                }

                url = $"https://www.googleapis.com/books/v1/volumes?q={Uri.EscapeDataString(query)}&maxResults=1";
            }

            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return "";

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

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("Verified Book Metadata (from Google Books search):");
                if (!string.IsNullOrEmpty(title)) sb.AppendLine($"Title: {title}");
                if (!string.IsNullOrEmpty(authors)) sb.AppendLine($"Author(s): {authors}");
                if (!string.IsNullOrEmpty(year)) sb.AppendLine($"Year: {year}");
                if (!string.IsNullOrEmpty(publisher)) sb.AppendLine($"Publisher: {publisher}");
                
                return sb.ToString();
            }
        }
        catch
        {
            // Ignore search network exceptions, just return empty to proceed gracefully
        }

        return "";
    }
}
