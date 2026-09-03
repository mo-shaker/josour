using System.Text.RegularExpressions;
using RouteBridge.Infrastructure;
using RouteBridge.Infrastructure.Logging;
using Serilog;

namespace RouteBridge.Infrastructure.Tests;

public sealed class LoggingSetupTests
{
    [Fact]
    public void DefaultPathTemplate_IsLocalAppData_RouteBridge_Logs_AppDashLog()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RouteBridge",
            "logs",
            "app-.log");

        Assert.Equal(expected, LoggingSetup.GetLogPathTemplate());
        Assert.Equal(Path.Combine(AppPaths.LocalAppDataRoot, "logs"), LoggingSetup.DefaultLogDirectory);
        Assert.EndsWith(Path.Combine("RouteBridge", "logs", "app-.log"), LoggingSetup.GetLogPathTemplate());
    }

    [Fact]
    public void Retention_IsFourteenDays()
    {
        Assert.Equal(14, LoggingSetup.RetainedFileCount);
    }

    [Fact]
    public void LogFileName_ForDate_UsesYyyyMmDd()
    {
        Assert.Equal("app-20260903.log", LoggingSetup.GetLogFileName(new DateOnly(2026, 9, 3)));
    }

    [Fact]
    public void Configuration_WritesDailyRollingFile_WithExpectedName()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rb-logs-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var logger = LoggingSetup.CreateConfiguration(dir).CreateLogger())
            {
                logger.Information("hello {Name}", "world");
            }

            var files = Directory.GetFiles(dir).Select(Path.GetFileName).ToArray();
            var file = Assert.Single(files);
            Assert.Matches(new Regex(@"^app-\d{8}\.log$"), file);
            Assert.Equal(LoggingSetup.GetLogFileName(DateOnly.FromDateTime(DateTime.Now)), file);

            var content = File.ReadAllText(Path.Combine(dir, file!));
            Assert.Contains("hello world", content);
            Assert.Contains("[INF]", content);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
