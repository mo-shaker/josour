using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Josour.Infrastructure.Device;

/// <summary>
/// Reads device identity from the environment. On Windows the OS name/build come from
/// <c>HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion</c>; elsewhere from <see cref="RuntimeInformation"/>.
/// Values are computed once and cached.
/// </summary>
public sealed class DeviceInfoProvider : IDeviceInfoProvider
{
    private const string FallbackVersion = "0.0.0";
    private const int FirstWindows11Build = 22000;
    private const int SwVersTimeoutMs = 1_000;

    private readonly Lazy<DeviceInfo> _info;

    /// <summary>Uses the entry assembly for the app version.</summary>
    public DeviceInfoProvider()
        : this(Assembly.GetEntryAssembly())
    {
    }

    /// <summary>Uses an explicit assembly for the app version (tests, or when the entry assembly is not the app).</summary>
    public DeviceInfoProvider(Assembly? appAssembly)
    {
        _info = new Lazy<DeviceInfo>(() => Build(appAssembly), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string DeviceName => _info.Value.DeviceName;

    public string OsVersion => _info.Value.OsVersion;

    public string OsBuild => _info.Value.OsBuild;

    public string AppVersion => _info.Value.AppVersion;

    public DeviceInfo GetDeviceInfo() => _info.Value;

    private static DeviceInfo Build(Assembly? appAssembly)
    {
        var (osVersion, osBuild) = true switch
        {
            _ when OperatingSystem.IsWindows() => ReadWindowsVersion(),
            _ when OperatingSystem.IsMacOS() => ReadMacVersion(),
            _ => (RuntimeInformation.OSDescription.Trim(), Environment.OSVersion.Version.ToString()),
        };

        return new DeviceInfo(ReadMachineName(), osVersion, osBuild, ResolveAppVersion(appAssembly));
    }

    private static string ReadMachineName()
    {
        try
        {
            var name = Environment.MachineName;
            return string.IsNullOrWhiteSpace(name) ? "unknown-device" : name;
        }
        catch (InvalidOperationException)
        {
            return "unknown-device";
        }
    }

    /// <summary>
    /// <c>sw_vers</c>: "macOS 26.6.2" and build "25G83".
    /// <para>
    /// Not <see cref="RuntimeInformation.OSDescription"/>, which on macOS reports the Darwin kernel version — "Darwin
    /// 25.6.0" — and that is not a number any administrator reading the device list, or any user reading a support
    /// bundle, can match to the macOS they believe they are running.
    /// </para>
    /// <para>The tool is absent or refused on no supported macOS, but if it ever is, the kernel description is a poor
    /// answer rather than no answer, and registering the device must not fail over a cosmetic field.</para>
    /// </summary>
    [SupportedOSPlatform("macos")]
    private static (string Version, string Build) ReadMacVersion()
    {
        var product = RunSwVers("-productName");
        var version = RunSwVers("-productVersion");
        var build = RunSwVers("-buildVersion");

        if (string.IsNullOrWhiteSpace(version))
        {
            return (RuntimeInformation.OSDescription.Trim(), Environment.OSVersion.Version.ToString());
        }

        var name = string.IsNullOrWhiteSpace(product) ? "macOS" : product;
        return ($"{name} {version}", string.IsNullOrWhiteSpace(build) ? version : build);
    }

    [SupportedOSPlatform("macos")]
    private static string RunSwVers(string argument)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("/usr/bin/sw_vers", argument)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return string.Empty;
            }

            var output = process.StandardOutput.ReadToEnd();
            // This runs once, at start-up, behind a Lazy; a second is a generous ceiling for a tool that prints one line.
            if (!process.WaitForExit(SwVersTimeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // It exited between the check and the kill.
                }

                return string.Empty;
            }

            return process.ExitCode == 0 ? output.Trim() : string.Empty;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return string.Empty;
        }
    }

    [SupportedOSPlatform("windows")]
    private static (string Version, string Build) ReadWindowsVersion()
    {
        string? productName = null;
        string? currentBuild = null;
        int? ubr = null;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            productName = key?.GetValue("ProductName") as string;
            currentBuild = key?.GetValue("CurrentBuild") as string;
            ubr = key?.GetValue("UBR") as int?;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            // Fall through to Environment-based values.
        }

        var buildNumber = int.TryParse(currentBuild, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : Environment.OSVersion.Version.Build;

        var version = string.IsNullOrWhiteSpace(productName) ? RuntimeInformation.OSDescription.Trim() : productName.Trim();

        // Windows 11 still reports "Windows 10 ..." in ProductName; builds >= 22000 are Windows 11.
        if (buildNumber >= FirstWindows11Build && version.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
        {
            version = version.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
        }

        var build = ubr is int revision
            ? string.Create(CultureInfo.InvariantCulture, $"{buildNumber}.{revision}")
            : buildNumber.ToString(CultureInfo.InvariantCulture);

        return (version, build);
    }

    private static string ResolveAppVersion(Assembly? appAssembly)
    {
        var assembly = appAssembly ?? typeof(DeviceInfoProvider).Assembly;

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // SourceLink appends "+<commit>"; keep the semantic part for display and for the server.
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus > 0 ? informational[..plus] : informational;
        }

        var version = assembly.GetName().Version;
        return version is null ? FallbackVersion : version.ToString(3);
    }
}
