using System.Text.Json;
using DevTools.Models;
using Xunit;

namespace DiskUsageAnalyzer.Tests;

public sealed class AppSettingsTests
{
    [Fact]
    public void GeneratedMetadata_PreservesLegacyKeysAndLauncherSettings()
    {
        const string existing = """
            {
              "isDiskUsageVisible": true,
              "refreshIntervalSeconds": 30,
              "runAsAdministrator": false,
              "processView": { "filterText": "git", "expandedSections": { "Background": true } }
            }
            """;

        var metadata = AppSettingsJsonContext.Default.AppSettings;
        var settings = Assert.IsType<AppSettings>(JsonSerializer.Deserialize(existing, metadata));
        Assert.True(settings.IsDiskUsageVisible);
        Assert.Equal(30, settings.RefreshIntervalSeconds);
        Assert.False(settings.RunAsAdministrator);
        Assert.True(settings.AutoRefreshEnabled);
        var processView = Assert.IsType<ProcessViewState>(settings.ProcessView);
        Assert.Equal("git", processView.FilterText);
        Assert.True(processView.ExpandedSections["Background"]);

        using var saved = JsonDocument.Parse(JsonSerializer.Serialize(settings, metadata));
        Assert.False(saved.RootElement.GetProperty("RunAsAdministrator").GetBoolean());
        Assert.True(saved.RootElement.GetProperty("IsDiskUsageVisible").GetBoolean());
        Assert.Equal("git", saved.RootElement.GetProperty("ProcessView").GetProperty("FilterText").GetString());
    }
}
