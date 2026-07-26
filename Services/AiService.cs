using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FileRenamer.Services;

public class RateLimitInfo
{
    public long LimitRequests { get; set; }
    public long RemainingRequests { get; set; }
    public long LimitTokens { get; set; }
    public long RemainingTokens { get; set; }
}

public class AiService
{
    public static event EventHandler<RateLimitInfo>? RateLimitUpdated;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    
    public string Provider { get; set; } = "Ollama";
    public string Endpoint { get; set; } = "http://localhost:11434/api/generate";
    public string Model { get; set; } = "gemma3:4b";
    public string OllamaApiKey { get; set; } = "";
    public string OpenAiApiKey { get; set; } = "";
    public string GoogleApiKey { get; set; } = "";

    public async Task<(string Category, string TargetSubfolder, string SuggestedName)> SuggestNameAsync(
        string originalName,
        string extension,
        string extractedText,
        int    pageCount      = 0,
        long   fileSizeBytes  = 0,
        bool   wasOcr         = false,
        string parentFolder   = "",
        string onlineMetadata = "",
        CancellationToken ct  = default)
    {
        string hints        = ContentHintExtractor.BuildHints(extractedText);
        const int textCap   = 8000;
        string trimmed      = extractedText.Length > textCap ? extractedText[..textCap] : extractedText;
        string metaBlock    = string.IsNullOrWhiteSpace(hints) ? "" : $"Extracted metadata:\n{hints}\n\n";
        if (!string.IsNullOrWhiteSpace(onlineMetadata))
        {
            metaBlock = onlineMetadata + "\n\n" + metaBlock;
        }

        // Build the file-context block
        var ctx = new System.Text.StringBuilder();
        ctx.AppendLine($"Original filename : \"{originalName}\" (type: {extension})");
        if (fileSizeBytes > 0)
            ctx.AppendLine($"File size         : {FormatSize(fileSizeBytes)}");
        if (pageCount > 0)
            ctx.AppendLine($"Pages read        : {pageCount}");
        if (wasOcr)
            ctx.AppendLine("Text source       : OCR (scanned document — text may contain noise)");
        if (!string.IsNullOrWhiteSpace(parentFolder))
            ctx.AppendLine($"Parent folder     : {parentFolder}");

        string prompt =
$@"You are an expert digital librarian and document cataloguing assistant.

Your job is to analyze the supplied document, extract reliable metadata, classify it, and generate ONE optimal filename and ONE storage path.

----------------------------
DOCUMENT
----------------------------

{ctx}

{metaBlock}

Document content (first ~{textCap} chars):

```
{trimmed}
```

==================================================
TASK 1 — IDENTIFY DOCUMENT TYPE
==================================================

Determine exactly ONE category.

Possible categories:

Books
Research Papers
Documentation
Courses
Notes
Projects
Software
Media
Personal
Finance
Government
Certificates
Archive
Unknown

==================================================
TASK 2 — EXTRACT METADATA
==================================================

Extract whenever possible:

• Title
• Subtitle
• Author(s)
• Edition
• Volume
• Part
• Series
• Publisher
• Year
• Organization
• Document Number
• Date
• Subject

Metadata priority (highest first):

1. Extracted metadata
2. Original filename
3. Document contents
4. OCR text

When conflicting information exists, trust higher-priority sources.

==================================================
TASK 3 — CLEAN METADATA
==================================================

Remove ALL noise including:

• ISBN10
• ISBN13
• DOI unless research paper
• MD5
• SHA
• UUID
• CRC
• Hashes
• URLs
• Website names
• Digital library names
• Watermarks
• Download source
• ""Anna's Archive""
• ""Libgen""
• ""Z-Library""
• ""OceanofPDF""
• ""PDFDrive""
• uploader names
• duplicate years
• duplicate editions

Correct OCR mistakes whenever obvious.

Normalize whitespace.

Normalize punctuation.

==================================================
TASK 4 — NORMALIZE AUTHOR NAMES
==================================================

Examples:

Robert C Martin
Martin Fowler
Al Sweigart
Gilbert Strang
Andrew Hunt and David Thomas
Erich Gamma, Richard Helm, Ralph Johnson and John Vlissides

Never use:

et al.
Group
Anonymous

unless absolutely unavoidable.

==================================================
TASK 5 — NORMALIZE EDITIONS
==================================================

Convert all editions to:

(2nd Edition)

(3rd Edition)

(4th Edition)

...

Examples:

Third Edition
3rd ed.
Edition 3
3e

↓

(3rd Edition)

==================================================
TASK 6 — GENERATE FILENAME
==================================================

Preferred format:

Title (Edition) - Author(s) (Year)

Examples:

Clean Code - Robert C Martin (2008)

The Pragmatic Programmer (2nd Edition) - Andrew Hunt and David Thomas (2020)

Automate the Boring Stuff with Python (3rd Edition) - Al Sweigart (2025)

Advanced Programming in the UNIX Environment (3rd Edition) - W Richard Stevens and Stephen A Rago (2013)

Rules

• Omit missing fields.
• Never invent metadata.
• Preserve subtitles when useful.
• Preserve Part, Volume and Series.
• Maximum filename length: 120 characters.
• If necessary shorten the title before shortening author names.
• Never truncate the year.
• Do NOT include file extension.

Allowed filename characters:

Letters
Numbers
Spaces
Hyphen (-)
Parentheses ()

Replace every invalid Windows filename character:

< > : "" / \ | ? *

with spaces.

Collapse repeated spaces.

==================================================
TASK 7 — KEEP RULE
==================================================

Return KEEP ONLY if ALL conditions are true:

• filename already follows the preferred format
• metadata is correct
• no hashes
• no ISBN
• no watermarks
• no URLs
• no duplicate years
• no duplicate edition
• no uploader names
• no unnecessary punctuation

Otherwise generate a cleaned filename.

==================================================
TASK 8 — DETERMINE LIBRARY PATH
==================================================

Choose ONE path.

Books\<Subject>

Research Papers\<Field>

Documentation\<Technology>

Courses\<Course>

Notes\<Topic>

Projects\<Project>

Software\<Application>

Media\<Subtype>

Personal\<Subtype>

Finance\<Subtype>

Government\<Subtype>

Certificates\<Subtype>

Archive\<YearOrTopic>

Unknown

Examples:

Books\Computer Science

Books\Machine Learning

Books\Mathematics

Books\Programming

Books\Operating Systems

Research Papers\Machine Learning

Documentation\Docker

Finance\Invoices

Government\Cards\PAN Card

==================================================
OUTPUT FORMAT
==================================================

Output EXACTLY ONE LINE.

[Category: <Category> | Path: <RelativePath>] <Filename>

Examples:

[Category: Books | Path: Books\Programming] Clean Code - Robert C Martin (2008)

[Category: Books | Path: Books\Operating Systems] Advanced Programming in the UNIX Environment (3rd Edition) - W Richard Stevens and Stephen A Rago (2013)

[Category: Government | Path: Government\Cards\PAN Card] PAN Card - Amitendu (2024)

[Category: Unknown | Path: Unknown] KEEP

Output ONLY the single line.

No markdown.

No explanation.

No quotes.";

        string rawResponse = "";

        if (Provider == "OpenAI")
        {
            if (string.IsNullOrWhiteSpace(OpenAiApiKey))
            {
                throw new InvalidOperationException("OpenAI API Key is not configured. Please add it in Settings.");
            }

            HttpResponseMessage? resp = null;
            int maxRetries = 3;
            int delayMs = 2000;

            for (int i = 0; i <= maxRetries; i++)
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
                request.Headers.Add("Authorization", $"Bearer {OpenAiApiKey}");

                var body = new
                {
                    model = Model,
                    messages = new[]
                    {
                        new { role = "user", content = prompt }
                    },
                    temperature = 0.0
                };
                request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

                resp = await _http.SendAsync(request, ct);
                if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests && i < maxRetries)
                {
                    int waitMs = delayMs;
                    if (resp.Headers.TryGetValues("Retry-After", out var values) && 
                        int.TryParse(values.FirstOrDefault(), out int seconds))
                    {
                        waitMs = seconds * 1000;
                    }
                    await Task.Delay(waitMs, ct);
                    delayMs *= 2;
                    continue;
                }
                break;
            }

            resp!.EnsureSuccessStatusCode();
            ParseRateLimitHeaders(resp.Headers);

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var choices = doc.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() > 0)
            {
                rawResponse = choices[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            }
        }
        else if (Provider == "Google")
        {
            if (string.IsNullOrWhiteSpace(GoogleApiKey))
            {
                throw new InvalidOperationException("Google AI Studio API Key is not configured. Please add it in Settings.");
            }

            HttpResponseMessage? resp = null;
            int maxRetries = 3;
            int delayMs = 3000;

            for (int i = 0; i <= maxRetries; i++)
            {
                var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Model}:generateContent?key={GoogleApiKey}";
                var body = new
                {
                    contents = new[]
                    {
                        new { parts = new[] { new { text = prompt } } }
                    },
                    generationConfig = new
                    {
                        temperature = 0.0
                    }
                };
                var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

                resp = await _http.PostAsync(url, content, ct);
                if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests && i < maxRetries)
                {
                    int waitMs = delayMs;
                    if (resp.Headers.TryGetValues("Retry-After", out var values) && 
                        int.TryParse(values.FirstOrDefault(), out int seconds))
                    {
                        waitMs = seconds * 1000;
                    }
                    await Task.Delay(waitMs, ct);
                    delayMs *= 2;
                    continue;
                }
                break;
            }

            resp!.EnsureSuccessStatusCode();
            ParseRateLimitHeaders(resp.Headers);

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var candidates = doc.RootElement.GetProperty("candidates");
            if (candidates.GetArrayLength() > 0)
            {
                var contentElem = candidates[0].GetProperty("content");
                var parts = contentElem.GetProperty("parts");
                if (parts.GetArrayLength() > 0)
                {
                    rawResponse = parts[0].GetProperty("text").GetString() ?? "";
                }
            }
        }
        else // Ollama
        {
            var body    = new { model = Model, prompt, stream = false };
            var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            if (!string.IsNullOrWhiteSpace(OllamaApiKey))
            {
                request.Headers.Add("Authorization", $"Bearer {OllamaApiKey}");
            }
            request.Content = content;

            var resp = await _http.SendAsync(request, ct);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            rawResponse = doc.RootElement.GetProperty("response").GetString() ?? "";
        }

        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            rawResponse = "KEEP";
        }

        return ParseCategoryAndName(rawResponse, originalName, extension);
    }

    public static (string Category, string TargetSubfolder, string SuggestedName) ParseCategoryAndName(string rawResponse, string originalName, string extension)
    {
        rawResponse = rawResponse.Trim().Trim('"', '\'', '`').Trim();
        
        string category = "Unknown";
        string path = "Unknown";
        string name = rawResponse;

        // Use regex to parse [Category: X | Path: Y] Z
        var match = Regex.Match(rawResponse, @"^\[Category:\s*([^|]+)\|\s*Path:\s*([^\]]+)\]\s*(.+)$", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (match.Success)
        {
            category = match.Groups[1].Value.Trim();
            path = match.Groups[2].Value.Trim();
            name = match.Groups[3].Value.Trim();
        }
        else
        {
            // Fallback for simple [Category] Name
            if (rawResponse.StartsWith("["))
            {
                int closeBracket = rawResponse.IndexOf(']');
                if (closeBracket > 0)
                {
                    category = rawResponse.Substring(1, closeBracket - 1).Trim();
                    name = rawResponse.Substring(closeBracket + 1).Trim();
                    path = category;
                }
            }
        }

        category = NormalizeCategory(category, extension);
        path = SanitizePath(path, category, extension);
        name = SanitizeName(name);

        if (string.IsNullOrWhiteSpace(name) || name.Equals("KEEP", StringComparison.OrdinalIgnoreCase))
        {
            name = SanitizeName(originalName);
        }

        return (category, path, name);
    }

    private static string SanitizePath(string path, string category, string extension)
    {
        if (string.IsNullOrEmpty(path) || path.Equals("Unknown", StringComparison.OrdinalIgnoreCase) || path.Equals("Other", StringComparison.OrdinalIgnoreCase))
        {
            return category; // fallback to top level category
        }

        // Clean invalid path characters
        var illegal = Path.GetInvalidPathChars().ToArray();
        foreach (char c in illegal)
        {
            path = path.Replace(c, '-');
        }

        return path.Trim();
    }

    private static string NormalizeCategory(string category, string extension)
    {
        category = category.ToLowerInvariant().Replace(" ", "").Replace("&", "");
        
        if (category.Contains("book") || category.Contains("literature") || category.Contains("novel") || category.Contains("textbook")) return "Books";
        if (category.Contains("paper") || category.Contains("research") || category.Contains("article") || category.Contains("journal") || category.Contains("thesis")) return "Research Papers";
        if (category.Contains("documentation") || category.Contains("manual") || category.Contains("guide") || category.Contains("api") || category.Contains("spec")) return "Documentation";
        if (category.Contains("course") || category.Contains("lecture") || category.Contains("class") || category.Contains("syllabus") || category.Contains("tutorial")) return "Courses";
        if (category.Contains("note") || category.Contains("memo") || category.Contains("draft") || category.Contains("meeting")) return "Notes";
        if (category.Contains("project") || category.Contains("code") || category.Contains("repo")) return "Projects";
        if (category.Contains("software") || category.Contains("app") || category.Contains("program") || category.Contains("exe")) return "Software";
        if (category.Contains("media") || category.Contains("music") || category.Contains("video") || category.Contains("audio") || category.Contains("song")) return "Media";
        if (category.Contains("personal") || category.Contains("resume") || category.Contains("cv") || category.Contains("medical") || category.Contains("health")) return "Personal";
        if (category.Contains("finance") || category.Contains("invoice") || category.Contains("receipt") || category.Contains("bill") || category.Contains("statement") || category.Contains("tax") || category.Contains("salary")) return "Finance";
        if (category.Contains("government") || category.Contains("card") || category.Contains("passport") || category.Contains("visa") || category.Contains("id") || category.Contains("pan")) return "Government";
        if (category.Contains("certificate") || category.Contains("degree") || category.Contains("diploma") || category.Contains("award") || category.Contains("achievement") || category.Contains("cert")) return "Certificates";
        if (category.Contains("archive") || category.Contains("backup") || category.Contains("old") || category.Contains("history")) return "Archive";
        
        // Fallback by extension
        string ext = extension.ToLowerInvariant();
        if (ext == ".pdf") return "Books";
        if (ext == ".docx" || ext == ".txt" || ext == ".md") return "Documentation";
        if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp") return "Media";
        
        return "Unknown";
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)        return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024):F1} MB";
    }

    /// <summary>Returns the names of all models retrieved dynamically from the selected provider's API.</summary>
    public async Task<List<string>> GetModelsAsync()
    {
        if (Provider == "OpenAI")
        {
            if (string.IsNullOrWhiteSpace(OpenAiApiKey))
            {
                return new List<string> { "gpt-4o-mini", "gpt-4o", "gpt-3.5-turbo", "o1-mini" };
            }

            var request = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
            request.Headers.Add("Authorization", $"Bearer {OpenAiApiKey}");

            using var resp = await _http.SendAsync(request);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var models = new List<string>();
            if (doc.RootElement.TryGetProperty("data", out var dataArr))
            {
                foreach (var m in dataArr.EnumerateArray())
                {
                    if (m.TryGetProperty("id", out var idProp))
                    {
                        string id = idProp.GetString() ?? "";
                        if (id.Contains("gpt") || id.StartsWith("o1") || id.StartsWith("o3"))
                        {
                            models.Add(id);
                        }
                    }
                }
            }
            return models.OrderBy(m => m).ToList();
        }
        else if (Provider == "Google")
        {
            if (string.IsNullOrWhiteSpace(GoogleApiKey))
            {
                return new List<string> { "gemini-1.5-flash", "gemini-2.0-flash", "gemini-2.5-flash", "gemini-1.5-pro" };
            }

            var url = $"https://generativelanguage.googleapis.com/v1beta/models?key={GoogleApiKey}";
            using var resp = await _http.GetAsync(url);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var models = new List<string>();
            if (doc.RootElement.TryGetProperty("models", out var modelsArr))
            {
                foreach (var m in modelsArr.EnumerateArray())
                {
                    bool supportsGenerate = false;
                    if (m.TryGetProperty("supportedGenerationMethods", out var methods))
                    {
                        foreach (var method in methods.EnumerateArray())
                        {
                            if (method.GetString() == "generateContent")
                            {
                                supportsGenerate = true;
                                break;
                            }
                        }
                    }

                    if (supportsGenerate && m.TryGetProperty("name", out var nameProp))
                    {
                        string name = nameProp.GetString() ?? "";
                        if (name.StartsWith("models/"))
                        {
                            name = name.Substring("models/".Length);
                        }

                        if (name.Contains("gemini", StringComparison.OrdinalIgnoreCase) && 
                            !name.Contains("vision", StringComparison.OrdinalIgnoreCase) && 
                            !name.Contains("embedding", StringComparison.OrdinalIgnoreCase) &&
                            !name.Contains("aqa", StringComparison.OrdinalIgnoreCase))
                        {
                            models.Add(name);
                        }
                    }
                }
            }
            return models.OrderBy(m => m).ToList();
        }
        else // Ollama
        {
            var tagsUrl = Endpoint.Replace("/api/generate", "/api/tags");
            var request = new HttpRequestMessage(HttpMethod.Get, tagsUrl);
            if (!string.IsNullOrWhiteSpace(OllamaApiKey))
            {
                request.Headers.Add("Authorization", $"Bearer {OllamaApiKey}");
            }

            using var resp = await _http.SendAsync(request);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var models = new List<string>();
            if (doc.RootElement.TryGetProperty("models", out var arr))
                foreach (var m in arr.EnumerateArray())
                    if (m.TryGetProperty("name", out var n))
                        models.Add(n.GetString() ?? "");

            return models.Where(m => !string.IsNullOrEmpty(m)).ToList();
        }
    }

    public async Task PullModelAsync(string modelName, Action<string> progressCallback, CancellationToken ct)
    {
        var pullUrl = Endpoint.Replace("/api/generate", "/api/pull");
        var body = new { name = modelName, stream = true };
        var request = new HttpRequestMessage(HttpMethod.Post, pullUrl);
        
        if (!string.IsNullOrWhiteSpace(OllamaApiKey))
        {
            request.Headers.Add("Authorization", $"Bearer {OllamaApiKey}");
        }

        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;

            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("status", out var statusProp))
            {
                string status = statusProp.GetString() ?? "";
                
                if (doc.RootElement.TryGetProperty("completed", out var compProp) && 
                    doc.RootElement.TryGetProperty("total", out var totProp) && 
                    totProp.GetInt64() > 0)
                {
                    double percent = (double)compProp.GetInt64() / totProp.GetInt64() * 100;
                    progressCallback($"{status} ({percent:F1}%)");
                }
                else
                {
                    progressCallback(status);
                }
            }
        }
    }

    private static string SanitizeName(string name)
    {
        // Take only the first line and strip surrounding quotes
        name = name.Trim().Trim('"', '\'', '`').Trim();
        name = name.Split('\n')[0].Trim();

        // Replace em-dash and en-dash with standard hyphens
        name = name.Replace("—", "-").Replace("–", "-");

        // Remove 32-char hex hashes (MD5 / UUIDs like ed3d25f26f400db1f9e92c22d0e727ea)
        name = Regex.Replace(name, @"\b[a-fA-F0-9]{32}\b", "", RegexOptions.IgnoreCase);

        // Remove ISBN strings (e.g. isbn13 9789356937529, isbn10 1234567890)
        name = Regex.Replace(name, @"\b(?:isbn13|isbn10|isbn)\s*[\d-]+\b", "", RegexOptions.IgnoreCase);

        // Remove common repository/website source watermarks if present
        string[] siteWatermarks = new[]
        {
            "Anna's Archive", "Anna’s Archive", "Anna's-Archive", "annas-archive",
            "Libgen", "Library Genesis", "Z-Library", "Z-Lib", "zlib", "zlib.pub",
            "OceanofPDF", "PDFDrive"
        };
        foreach (var tag in siteWatermarks)
        {
            name = Regex.Replace(name, @"\s*[-–—]?\s*" + Regex.Escape(tag) + @"\b", "", RegexOptions.IgnoreCase);
        }

        // Replace duplicate consecutive years (e.g., "2023, 2023" or "2023 2023")
        name = Regex.Replace(name, @"\b(\d{4})\s*[\s,]+\s*\1\b", "$1");

        // Replace underscores with spaces as requested
        name = name.Replace('_', ' ');

        // Replace illegal filesystem chars — keep spaces, hyphens, parentheses
        var illegal = Path.GetInvalidFileNameChars()
                          .Except(new[] { ' ', '-', '(', ')' })
                          .ToArray();
        foreach (char c in illegal)
            name = name.Replace(c, '-');

        // Collapse multiple spaces/hyphens
        while (name.Contains("--")) name = name.Replace("--", "-");
        while (name.Contains(" - - ")) name = name.Replace(" - - ", " - ");
        while (name.Contains("  ")) name = name.Replace("  ", " ");

        return name.Trim('-', ' ');
    }

    private static void ParseRateLimitHeaders(HttpResponseHeaders headers)
    {
        try
        {
            var info = new RateLimitInfo();
            bool hasData = false;

            if (headers.TryGetValues("x-ratelimit-limit-requests", out var lmReq))
            {
                if (long.TryParse(lmReq.FirstOrDefault(), out long val)) { info.LimitRequests = val; hasData = true; }
            }
            if (headers.TryGetValues("x-ratelimit-remaining-requests", out var remReq))
            {
                if (long.TryParse(remReq.FirstOrDefault(), out long val)) { info.RemainingRequests = val; hasData = true; }
            }
            if (headers.TryGetValues("x-ratelimit-limit-tokens", out var lmTok))
            {
                if (long.TryParse(lmTok.FirstOrDefault(), out long val)) { info.LimitTokens = val; hasData = true; }
            }
            if (headers.TryGetValues("x-ratelimit-remaining-tokens", out var remTok))
            {
                if (long.TryParse(remTok.FirstOrDefault(), out long val)) { info.RemainingTokens = val; hasData = true; }
            }

            if (hasData)
            {
                RateLimitUpdated?.Invoke(null, info);
            }
        }
        catch
        {
            // Ignore header parsing errors
        }
    }
}
