using System;
using System.IO;
using System.Text.Json;

namespace FileRenamer.Services;

public class AppSettings
{
    public string Provider { get; set; } = "Ollama";
    public string OllamaEndpoint { get; set; } = "http://localhost:11434/api/generate";
    public string OllamaModel { get; set; } = "gemma3:4b";
    public string OllamaApiKey { get; set; } = "";
    public string OpenAiApiKey { get; set; } = "";
    public string OpenAiModel { get; set; } = "gpt-4o-mini";
    public string GoogleApiKey { get; set; } = "";
    public string GoogleModel { get; set; } = "gemini-1.5-flash";
}

public static class SettingsService
{
    private static readonly string FolderPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FileRenamer");
    private static readonly string FilePath = Path.Combine(FolderPath, "settings.json");

    public static AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            // Fallback to default settings in case of read or parse error
        }
        return new AppSettings();
    }

    public static void SaveSettings(AppSettings settings)
    {
        try
        {
            if (!Directory.Exists(FolderPath))
            {
                Directory.CreateDirectory(FolderPath);
            }
            string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // Ignore write/permission errors
        }
    }
}
