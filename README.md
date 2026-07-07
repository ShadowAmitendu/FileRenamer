# FileRenamer (WinUI3 + Ollama)

## Setup
1. Open in Visual Studio 2022 (Windows App SDK workload installed).
2. Restore NuGet packages (auto on build).
3. Download `eng.traineddata` (Tesseract 4/5 fast model) from:
   https://github.com/tesseract-ocr/tessdata_fast
   Place it in a `tessdata/` folder next to `FileRenamer.csproj` (already wired
   into the .csproj to copy to output).
4. Install & run Ollama, pull a model: `ollama pull gemma3:4b`
5. Build & run (x64 recommended).

## Behavior
- Select a folder (or single file) and which file types to process.
- Content is read per file:
  - **PDF**: unlocks/removes owner-permission restrictions automatically.
    Files needing an actual open password are skipped (status: "Skipped
    (Password protected)"). Reads up to 10 pages; scanned pages with no
    text layer fall back to OCR automatically.
  - **DOCX**: extracted via OpenXML (~10 "pages" worth of paragraphs).
  - **TXT/MD/CSV**: first ~10 pages worth of characters.
  - **Images**: OCR'd directly.
  - **Other**: only checked if you enable "Other (name only)" — no content
    read, filename/extension only.
- Extracted text + a few heuristic hints (detected dates, invoice/ref IDs,
  probable title line) are sent to your local Ollama model to suggest a
  clean filename.
- Review/edit suggestions, then "Rename Selected" or "Rename All".

## Notes
- iText7 is AGPL-licensed — fine for personal/local use; check licensing
  before any commercial redistribution.
- OCR requires the native Tesseract binaries that ship with the `Tesseract`
  NuGet package (win-x64) — matching your target platform.
