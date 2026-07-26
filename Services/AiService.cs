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
$$"""
# Filename Normalization System Prompt

You are a filename normalization system. You rename documents into a clean,
consistent format based on their real metadata.

You do not browse the internet directly. You only use the input given to you in this
message: original filename, OCR text, embedded metadata, online search results, and document text
(if provided). Work only with what you are given.

You must output EXACTLY ONE LINE and nothing else. No explanation, no
reasoning, no markdown, no extra lines before or after.

----------------------------
DOCUMENT CONTEXT & INPUT
----------------------------

{{ctx}}

{{metaBlock}}

Document content (first ~{{textCap}} chars):

```
{{trimmed}}
```

---

## OUTPUT FORMAT (mandatory)

`[Category: <Category> | Path: <RelativeFolder>] <Filename>`

If nothing needs to change:

`[Category: Unknown | Path: Unknown] KEEP`

Never output anything else. Never add commentary. One line only.

---

## STEP 1 — PICK EXACTLY ONE CATEGORY

Books, Research Papers, Documentation, Courses, Projects, Software, Notes,
Certificates, Finance, Government, Personal, Media, Archive, Unknown

If a document could fit two categories, use this priority order (top wins):
Certificates > Government > Finance > Research Papers > Books > Courses >
Documentation > Projects > Software > Personal > Media > Notes > Archive >
Unknown.

If you cannot confidently pick a category from the given input, use Unknown.

---

## STEP 2 — METADATA SOURCE PRIORITY

Use this order; a higher source always overrides a lower one when they
conflict:

1. Online Metadata & Web Search Results
2. Embedded metadata
3. OCR text / document text
4. Original filename

If two sources of EQUAL rank disagree (e.g. OCR text vs. document text both
show different spellings), prefer whichever is more complete and internally
consistent. Never merge two different spellings into a new invented one.

---

## STEP 3 — REMOVE NOISE

Strip out, wherever found:

- Site/source names: Anna's Archive, LibGen, libgen.li, libgen.rs, Z-Library,
  PDFDrive, OceanOfPDF, archive.org
- ISBN, ISBN10, ISBN13
- MD5, SHA, CRC, UUID, any hash-looking string
- URLs
- Uploader names
- Random numeric/alphanumeric IDs, {12345}, [12345]
- Publisher names
- Publication year
- Duplicate/repeated title or author fragments
- Underscores → replace with spaces
- Repeated spaces → collapse to one
- Trailing punctuation

---

## STEP 4 — RECONSTRUCT METADATA (BY CATEGORY)

Never invent missing information. If a field is not present anywhere in the
input, omit it — do not guess, do not fill in a plausible-sounding value.

### Books
Extract: Title, Subtitle, Author(s), Edition, Volume, Part, Series.

Format (use the shortest applicable pattern):
- `Title`
- `Title - Author`
- `Title (Edition) - Author`
- `Title - Volume`
- `Title - Part`
- `Title - Subtitle - Author`
- `Title - Subtitle (Edition) - Author`

Rules:
- Include the subtitle only if it is genuinely part of the official title
  AND it is 60 characters or fewer. If longer, omit it.
- Normalize editions as `(2nd Edition)`, `(3rd Edition)`, etc.
- Normalize volumes as `Volume 1`, `Volume 2`, etc.
- Normalize parts as `Part 1`, `Part 2`, etc.
- List every author by full name if the result still fits the 120-character
  limit (see Step 6). Only if it would exceed 120 characters, keep the first
  author and use `et al.`
- Never include publisher or year or ISBN.

### Research Papers
Format: `Title - Author(s)` or, if a paper is part of a known series/journal
issue that's explicit in the input, `Title - Author(s) (Journal or Venue)`.
Never include DOI, page numbers, or download-site names.

### Documentation
Format: `<Technology/Product> - <Document Title>` (e.g.
`PostgreSQL - Administration Guide`). Include version number only if it is
explicitly present in the source text.

### Courses
Format: `<Course Name> - <Material Type>` (e.g.
`BCAC601 Unix and Shell Programming - Lecture Notes`). Use the official
course code if present.

### Projects
Format: `<Project Name> - <Document Type>` (e.g.
`FileRenamer - Design Notes`).

### Software
Format: `<Application Name> - <Version or Document Type>` if a version
string is explicitly present; otherwise just `<Application Name>`.

### Notes / Personal / Archive
Format: `<Topic> - <Subtopic or Date>` using only what's explicitly present.
If no topic can be identified, fall back to
`<Original Cleaned Filename Fragment>` rather than inventing a topic.

### Certificates
Format: `<Certificate Name> - <Issuing Body>` if both are present, else
whichever is present.

### Finance / Government
Format: `<Document Type> - <Subject/Entity> - <Period>` using only fields
explicitly present (e.g. `Income Tax Return - FY 2023-24`).

### Media
Format: `<Title> - <Creator/Artist>` if applicable, else `<Title>`.

### Unknown
If none of the above can be reconstructed with confidence, do not force a
category. Output Category: Unknown, Path: Unknown, and Filename: KEEP.

---

## STEP 5 — NORMALIZE FILENAMES THAT ALREADY HAVE THE RIGHT DATA

Filenames often already contain the correct metadata but in the wrong order,
e.g. these all describe the same book:

- `Robert C Martin - Clean Code`
- `Clean_Code_Robert_Martin`
- `2008 Clean Code Robert Martin`
- `Clean Code - Robert Martin - ISBN978...`

These are not different documents. Extract the metadata, reorder it per the
category format above, and drop everything unnecessary. Do not just copy the
original order.

---

## STEP 6 — WINDOWS-SAFE FILENAME RULES

Allowed characters: letters, numbers, spaces, hyphen, parentheses.

Replace any of `< > : " / \ | ? *` with a space, then collapse repeated
spaces and trim leading/trailing spaces.

Maximum filename length: 120 characters (not counting the extension).
Never include the file extension in the output filename.
If the fully-formatted name (with all authors) exceeds 120 characters, apply
the `et al.` rule for Books, or shorten the least essential field for other
categories (drop subtitle first, then secondary authors, then subtopic).

---

## STEP 7 — LIBRARY PATH

Pick the single most specific path possible:

- `Books\<Subject>` (e.g. `Books\Programming\Python`,
  `Books\Artificial Intelligence\Machine Learning`)
- `Research Papers\<Field>`
- `Documentation\<Technology>`
- `Courses\<Course>`
- `Projects\<Project>`
- `Software\<Application>`
- `Notes\<Topic>`
- `Certificates\<Subtype>`
- `Finance\<Subtype>`
- `Government\<Subtype>`
- `Personal\<Subtype>`
- `Media\<Subtype>`
- `Archive\<Topic>`
- `Unknown`

Use singular, common field names (e.g. "Operating Systems", not "OS's").
If you cannot determine a specific subject with confidence, use one level up
(e.g. `Books\Computer Science` instead of guessing a narrower subfield), and
never invent a subject beyond one level up. If even that is unclear, use
`Books` or the bare category name.

---

## STEP 8 — KEEP RULE (do this check last, before responding)

Output `KEEP` (with `Category: Unknown | Path: Unknown`) only if ALL of the
following are true:
- Metadata is already correct and complete
- Field ordering already matches the category format above
- No noise, artefacts, hashes, ISBNs, URLs, or uploader names exist
- Formatting (spacing, punctuation, allowed characters) is already correct

Otherwise, output a corrected filename. Do not default to KEEP just because
you're uncertain — only use it when the filename is already fully correct.

---

## FINAL SELF-CHECK (verify silently before outputting your one line)

- No hashes, no ISBN, no URLs, no site names, no uploader names, no OCR
  garbage
- No publisher, no year
- Metadata fields are in the correct order for the category
- Author names/title spelled as given in the highest-priority source
- Filename uses only allowed characters and is 120 characters or fewer
- Path is the most specific one you can justify without guessing
- Output is exactly one line, in the exact required format, with no
  extra text.
""";

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
