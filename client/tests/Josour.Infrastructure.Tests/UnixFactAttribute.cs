namespace Josour.Infrastructure.Tests;

/// <summary>A <see cref="FactAttribute"/> that skips itself on Windows, where file modes do not exist.</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "POSIX file modes only";
        }
    }
}
