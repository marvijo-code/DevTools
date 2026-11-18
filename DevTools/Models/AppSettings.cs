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

    internal static AppSettings? Read(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize(File.ReadAllText(path), AppSettingsJsonContext.Default.AppSettings)
        : null;
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext
{
}
