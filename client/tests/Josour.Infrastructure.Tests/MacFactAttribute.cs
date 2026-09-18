namespace Josour.Infrastructure.Tests;

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

/// <summary>The <see cref="TheoryAttribute"/> counterpart of <see cref="MacFactAttribute"/>.</summary>
public sealed class MacTheoryAttribute : TheoryAttribute
{
    public MacTheoryAttribute()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Skip = "macOS only";
        }
    }
}
