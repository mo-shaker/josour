using Android.Content;
using Android.OS;
using Josour.Infrastructure.Device;

namespace Josour.Droid.Platform;

/// <summary>What the server shows for this phone in the device list and the admin dashboard: "Samsung SM-S918B", "Android 15".</summary>
internal sealed class AndroidDeviceInfo : IDeviceInfoProvider
{
    private readonly Lazy<DeviceInfo> _info;

    public AndroidDeviceInfo(Context context) => _info = new Lazy<DeviceInfo>(() => Read(context));

    public string DeviceName => _info.Value.DeviceName;
    public string OsVersion => _info.Value.OsVersion;
    public string OsBuild => _info.Value.OsBuild;
    public string AppVersion => _info.Value.AppVersion;
    public DeviceInfo GetDeviceInfo() => _info.Value;

    private static DeviceInfo Read(Context context)
    {
        // The name the owner gave the phone ("Ahmed's phone") when there is one; otherwise maker and model.
        string? given = null;
        try { given = Android.Provider.Settings.Global.GetString(context.ContentResolver, "device_name"); }
        catch (Exception) { }

        var maker = Build.Manufacturer ?? string.Empty;
        var model = Build.Model ?? string.Empty;
        var hardware = model.StartsWith(maker, StringComparison.OrdinalIgnoreCase) ? model : $"{Capitalise(maker)} {model}".Trim();
        var name = string.IsNullOrWhiteSpace(given) ? hardware : given.Trim();

        string version = "0.0.0";
        try
        {
            var package = context.PackageManager?.GetPackageInfo(context.PackageName ?? string.Empty, 0);
            if (!string.IsNullOrEmpty(package?.VersionName)) version = package.VersionName!;
        }
        catch (Exception) { }

        return new DeviceInfo(
            Truncate(name, 100),
            $"Android {Build.VERSION.Release}",
            $"API {(int)Build.VERSION.SdkInt} · {hardware}",
            version);
    }

    private static string Capitalise(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
