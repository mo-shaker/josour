using Josour.Infrastructure.Device;

namespace Josour.Infrastructure.Tests.Support;

public sealed class FakeDeviceInfoProvider : IDeviceInfoProvider
{
    private readonly DeviceInfo _info = new("TEST-PC", "Windows 11 Pro", "22631.3593", "0.2.0-test");

    public string DeviceName => _info.DeviceName;

    public string OsVersion => _info.OsVersion;

    public string OsBuild => _info.OsBuild;

    public string AppVersion => _info.AppVersion;

    public DeviceInfo GetDeviceInfo() => _info;
}
