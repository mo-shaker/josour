namespace Josour.Proxy.Tests;

/// <summary>A <see cref="FactAttribute"/> that skips itself off macOS, with the reason visible in the run.</summary>
public sealed class MacFactAttribute : FactAttribute
{
    public MacFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Skip = "macOS only";
        }
    }
}
