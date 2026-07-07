using iText.Kernel.Exceptions;
using iText.Kernel.Pdf;

namespace FileRenamer.Services;

public enum PdfAccessResult
{
    Ok,              // readable, no restrictions (or restrictions removed)
    PasswordRequired // real open-password, cannot proceed, skip file
}

public static class PdfPermissionHelper
{
    /// <summary>
    /// Tries to open the PDF ignoring owner-permission restrictions.
    /// If it opens (no user/open password needed), re-saves a decrypted copy
    /// with restrictions stripped and returns that path.
    /// If an actual open password is required, returns PasswordRequired and skips.
    /// </summary>
    public static (PdfAccessResult result, string workingPath) PrepareUnlocked(string sourcePath)
    {
        try
        {
            var props = new ReaderProperties();

            // Probe: can we open at all without a user password?
            using (var probeReader = new PdfReader(sourcePath, props))
            {
                probeReader.SetUnethicalReading(true); // ignore owner-only permission flags
                using var probeDoc = new PdfDocument(probeReader);
                // reaching here = no open password required
            }

            // Re-save without encryption so permissions/restrictions are gone.
            string outPath = Path.Combine(Path.GetTempPath(), $"unlocked_{Guid.NewGuid():N}.pdf");
            using (var reader = new PdfReader(sourcePath, props))
            {
                reader.SetUnethicalReading(true);
                using var writer = new PdfWriter(outPath); // no EncryptionProperties -> plain output
                using var doc = new PdfDocument(reader, writer);
            }

            return (PdfAccessResult.Ok, outPath);
        }
        catch (BadPasswordException)
        {
            return (PdfAccessResult.PasswordRequired, sourcePath);
        }
        catch
        {
            // Not encrypted / any other issue reading as encrypted doc - use original as-is.
            return (PdfAccessResult.Ok, sourcePath);
        }
    }
}
