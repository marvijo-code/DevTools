using System.Text;
using System.Text.Json;
using DevTools.Models;
using Xunit;

namespace DiskUsageAnalyzer.Tests;

public sealed class AppSettingsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_PreservesLegacyKeysAndLauncherSettings(bool useLegacyEncoding)
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
        var file = Path.GetTempFileName();
        AppSettings settings;
        try
        {
            File.WriteAllText(file, existing, useLegacyEncoding ? Encoding.Unicode : Encoding.UTF8);
            settings = Assert.IsType<AppSettings>(AppSettings.Read(file));
        }
        finally
        {
            File.Delete(file);
        }
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
