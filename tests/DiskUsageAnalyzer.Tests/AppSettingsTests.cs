using System.Text;
using System.Text.Json;
using DevTools.Models;
using Xunit;

namespace DiskUsageAnalyzer.Tests;

public sealed class AppSettingsTests
{
    [Theory]
    [InlineData(65001, false)]
    [InlineData(65001, true)]
    [InlineData(1200, true)]
    [InlineData(1201, true)]
    [InlineData(12000, true)]
    [InlineData(12001, true)]
    public void Read_PreservesLegacyKeysAndLauncherSettings(int codePage, bool writePreamble)
    {
        const string existing = """
            {
              "isDiskUsageVisible": true,
              "refreshIntervalSeconds": 30,
              "runAsAdministrator": false,
              "processView": { "filterText": "gít", "expandedSections": { "Background": true } }
            }
            """;

        var metadata = AppSettingsJsonContext.Default.AppSettings;
        var file = Path.GetTempFileName();
        AppSettings settings;
        try
        {
            var encoding = writePreamble ? Encoding.GetEncoding(codePage) : new UTF8Encoding(false);
            Assert.Equal(writePreamble, !encoding.Preamble.IsEmpty);
            File.WriteAllText(file, existing, encoding);
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
        Assert.Equal("gít", processView.FilterText);
        Assert.True(processView.ExpandedSections["Background"]);

        using var saved = JsonDocument.Parse(JsonSerializer.Serialize(settings, metadata));
        Assert.False(saved.RootElement.GetProperty("RunAsAdministrator").GetBoolean());
        Assert.True(saved.RootElement.GetProperty("IsDiskUsageVisible").GetBoolean());
        Assert.Equal("gít", saved.RootElement.GetProperty("ProcessView").GetProperty("FilterText").GetString());
    }
}
