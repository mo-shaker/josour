using System.Text;
using Serilog;
using Serilog.Events;

namespace RouteBridge.Infrastructure.Logging;

/// <summary>
/// Serilog configuration shared by the app (and asserted by tests): rolling daily files
/// <c>%LOCALAPPDATA%\RouteBridge\logs\app-yyyyMMdd.log</c>, 14 days retained, plus the Debug sink.
/// <para>Rule: never log secrets (tokens, device secret, session secret) or tunnel payloads. Log key names, sizes and outcomes only.</para>
/// </summary>
public static class LoggingSetup
{
    public const string FileNamePrefix = "app-";
    public const string FileExtension = ".log";
    public const int RetainedFileCount = 14;
    public const long FileSizeLimitBytes = 50L * 1024 * 1024;

    public const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    /// <summary><c>%LOCALAPPDATA%\RouteBridge\logs</c>.</summary>
    public static string DefaultLogDirectory => AppPaths.LogsDirectory;

    /// <summary>
    /// The path template handed to the file sink, e.g. <c>...\logs\app-.log</c>.
    /// Serilog inserts the date before the extension: <c>app-20260903.log</c>.
    /// </summary>
    public static string GetLogPathTemplate(string? logDirectory = null) =>
        Path.Combine(logDirectory ?? DefaultLogDirectory, FileNamePrefix + FileExtension);

    /// <summary>File name the sink produces for a given day, e.g. <c>app-20260903.log</c>.</summary>
    public static string GetLogFileName(DateOnly date) =>
        FileNamePrefix + date.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture) + FileExtension;

    /// <summary>Builds the logger configuration. Call <c>.CreateLogger()</c> on the result.</summary>
    public static LoggerConfiguration CreateConfiguration(string? logDirectory = null, LogEventLevel minimumLevel = LogEventLevel.Debug) =>
        new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .WriteTo.File(
                GetLogPathTemplate(logDirectory),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: RetainedFileCount,
                fileSizeLimitBytes: FileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                outputTemplate: OutputTemplate,
                encoding: Encoding.UTF8)
            .WriteTo.Debug(outputTemplate: OutputTemplate);
}
