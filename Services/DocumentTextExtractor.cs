using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;

namespace FileRenamer.Services;

public class ExtractionResult
{
    public string Text { get; set; } = "";
    public int PagesRead { get; set; }
    public bool WasOcr { get; set; }
    public bool Skipped { get; set; }
    public string SkipReason { get; set; } = "";
}

public static class DocumentTextExtractor
{
    public const int MinPages = 10;
    private const int OcrTextThreshold = 20; // below this char count per page, assume scanned

    public static ExtractionResult Extract(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();

        return ext switch
        {
            ".pdf" => ExtractPdf(path),
            ".docx" => ExtractDocx(path),
            ".txt" or ".md" or ".log" or ".csv" => ExtractPlainText(path),
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tif" or ".tiff" => ExtractImage(path),
            _ => new ExtractionResult { Text = "", PagesRead = 0 }
        };
    }

    private static ExtractionResult ExtractPdf(string path)
    {
        var (access, workingPath) = PdfPermissionHelper.PrepareUnlocked(path);
        if (access == PdfAccessResult.PasswordRequired)
        {
            return new ExtractionResult { Skipped = true, SkipReason = "Password protected" };
        }

        var sb = new System.Text.StringBuilder();
        bool anyOcr = false;
        int pagesRead = 0;

        try
        {
            try
            {
                using var doc = PdfDocument.Open(workingPath);
                int totalPages = doc.NumberOfPages;
                int pagesToRead = Math.Min(MinPages, totalPages);

                for (int i = 1; i <= pagesToRead; i++)
                {
                    var page = doc.GetPage(i);
                    string pageText = page.Text ?? "";

                    if (pageText.Trim().Length < OcrTextThreshold)
                    {
                        // likely a scanned page - fall back to OCR
                        try
                        {
                            pageText = OcrService.OcrPdfPage(workingPath, i - 1); // 0-indexed for renderer
                            anyOcr = true;
                        }
                        catch
                        {
                            // OCR failed, keep whatever text we had
                        }
                    }

                    sb.AppendLine(pageText);
                    pagesRead++;
                }
            }
            catch (Exception ex)
            {
                // Structural parsing failed (e.g. malformed PDF dictionary). Fall back to OCR the first page.
                try
                {
                    string firstPageText = OcrService.OcrPdfPage(workingPath, 0);
                    sb.AppendLine(firstPageText);
                    pagesRead = 1;
                    anyOcr = true;
                }
                catch
                {
                    // If OCR also fails, rethrow the original parsing error
                    throw new Exception($"PDF structure is corrupt, and OCR fallback failed. Original error: {ex.Message}", ex);
                }
            }
        }
        finally
        {
            if (workingPath != path)
            {
                try { File.Delete(workingPath); } catch { /* ignore */ }
            }
        }

        return new ExtractionResult { Text = sb.ToString(), PagesRead = pagesRead, WasOcr = anyOcr };
    }

    private static ExtractionResult ExtractDocx(string path)
    {
        using var wordDoc = WordprocessingDocument.Open(path, false);
        var body = wordDoc.MainDocumentPart?.Document?.Body;
        if (body == null) return new ExtractionResult();

        var paragraphs = body.Elements<Paragraph>().Select(p => p.InnerText);
        // approximate "pages" as chunks of ~40 paragraphs since docx has no fixed page model
        var taken = paragraphs.Take(MinPages * 40);
        string text = string.Join("\n", taken);
        return new ExtractionResult { Text = text, PagesRead = MinPages };
    }

    private static ExtractionResult ExtractPlainText(string path)
    {
        string all = File.ReadAllText(path);
        // approximate a "page" as ~3000 chars
        int cap = MinPages * 3000;
        string text = all.Length > cap ? all[..cap] : all;
        return new ExtractionResult { Text = text, PagesRead = MinPages };
    }

    private static ExtractionResult ExtractImage(string path)
    {
        string text = OcrService.OcrImageFile(path);
        return new ExtractionResult { Text = text, PagesRead = 1, WasOcr = true };
    }
}
