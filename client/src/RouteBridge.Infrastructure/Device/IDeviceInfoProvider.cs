namespace RouteBridge.Infrastructure.Device;

/// <summary>Snapshot of the fields sent in <c>POST /auth/login</c> (<c>device.name/os_version/os_build</c>) and <c>hello.app_version</c>.</summary>
public sealed record DeviceInfo(string DeviceName, string OsVersion, string OsBuild, string AppVersion);

/// <summary>Describes this machine and this build of the app for device registration and diagnostics.</summary>
public interface IDeviceInfoProvider
{
    /// <summary>Machine name (<see cref="Environment.MachineName"/>), e.g. <c>LAPTOP-01</c>.</summary>
    string DeviceName { get; }

    /// <summary>Marketing OS name, e.g. <c>Windows 11 Pro</c>; falls back to <c>RuntimeInformation.OSDescription</c> off Windows.</summary>
    string OsVersion { get; }

    /// <summary>OS build, e.g. <c>22631.3593</c> (CurrentBuild.UBR) on Windows.</summary>
    string OsBuild { get; }

    /// <summary>App version from the entry assembly's informational version, e.g. <c>0.1.0</c>.</summary>
    string AppVersion { get; }

    DeviceInfo GetDeviceInfo();
}
