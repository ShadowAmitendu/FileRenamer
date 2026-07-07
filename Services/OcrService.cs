using PDFtoImage;
using SkiaSharp;
using Tesseract;

namespace FileRenamer.Services;

public static class OcrService
{
    // tessdata folder must sit next to the .exe, containing eng.traineddata
    private static readonly string TessDataPath =
        Path.Combine(AppContext.BaseDirectory, "tessdata");

    public static string OcrImageFile(string imagePath)
    {
        using var engine = new TesseractEngine(TessDataPath, "eng", EngineMode.Default);
        using var img = Pix.LoadFromFile(imagePath);
        using var page = engine.Process(img);
        return page.GetText();
    }

    public static string OcrPdfPage(string pdfPath, int pageIndex)
    {
        byte[] pdfBytes = File.ReadAllBytes(pdfPath);
        using SKBitmap bitmap = Conversion.ToImage(pdfBytes, page: (System.Index)pageIndex, options: new(Dpi: 200));

        string tempPng = Path.Combine(Path.GetTempPath(), $"ocr_{Guid.NewGuid():N}.png");
        using (var fs = File.OpenWrite(tempPng))
        using (var data = bitmap.Encode(SKEncodedImageFormat.Png, 90))
        {
            data.SaveTo(fs);
        }

        try
        {
            return OcrImageFile(tempPng);
        }
        finally
        {
            try { File.Delete(tempPng); } catch { /* ignore */ }
        }
    }
}
