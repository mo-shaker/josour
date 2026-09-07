namespace Josour.App.Services;

/// <summary>"Start with Windows" via the per-user Run key.</summary>
public interface IStartupRegistration
{
    /// <summary>False off Windows (development only).</summary>
    bool IsSupported { get; }

    bool IsEnabled { get; }

    void SetEnabled(bool enabled);
}
