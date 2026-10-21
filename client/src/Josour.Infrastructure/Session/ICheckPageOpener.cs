namespace Josour.Infrastructure.Session;

/// <summary>
/// Opens the guest's check page (http://check.josour/) in the user's default browser. Its arrival through the local proxy
/// shows that browser follows the system proxy; the page itself names the address sites will see.
/// </summary>
public interface ICheckPageOpener
{
    /// <summary>Does the user want the page opened as soon as a session starts (a setting)?</summary>
    bool OpenOnConnect { get; }

    /// <summary>Opens the page; false when it could not be opened.</summary>
    bool TryOpen(string url);
}
