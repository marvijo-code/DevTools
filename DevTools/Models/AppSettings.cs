using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevTools.Models;


internal sealed class AppSettings
{
    public ProcessViewState? ProcessView { get; set; }
    public bool AutoRefreshEnabled { get; set; } = true;

    public int RefreshIntervalSeconds { get; set; } = 10;

    public string? ActiveTab { get; set; }

    public bool IsDiskUsageVisible { get; set; }

    public string? EncryptedPersonalAccessToken { get; set; }

    public string? BiggestFilesScanRoot { get; set; }

    public int BiggestFilesTopCount { get; set; } = 100;

    public bool ResourceMonitoringEnabled { get; set; } = true;

    public int ResourceIntervalSeconds { get; set; } = 1;

    /// <summary>Read by Start-DevTools.ps1: start DevTools with administrator rights.</summary>
    public bool RunAsAdministrator { get; set; } = true;

    internal static AppSettings? Read(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var data = File.ReadAllBytes(path);
        ReadOnlySpan<byte> json = data;
        if (json is [0xEF, 0xBB, 0xBF, ..])
        {
            json = json[3..];
        }
        else if (json is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..] or [0, 0, 0xFE, 0xFF, ..])
        {
            // Older settings can carry a UTF-16 or UTF-32 byte order mark.
            using var reader = new StreamReader(new MemoryStream(data, writable: false));
            return JsonSerializer.Deserialize(reader.ReadToEnd(), AppSettingsJsonContext.Default.AppSettings);
        }

        return JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);
    }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext
{
}
