using Josour.Infrastructure.Api;

namespace Josour.Infrastructure.Settings;

/// <summary>Which window the app opens when it starts.</summary>
public enum StartupDestination
{
    /// <summary>The guided first run: server address → sign in, with a readiness summary at the end.</summary>
    FirstRun,

    /// <summary>The plain sign-in window: the server address is already known and only the credentials are missing.</summary>
    SignIn,

    /// <summary>Signed in: the main window (or the tray alone when the app was started minimized).</summary>
    MainWindow,
}

/// <summary>What <see cref="StartupPlanner"/> decided, and the state the chosen window opens with.</summary>
/// <param name="Destination">The window to show.</param>
/// <param name="ServerUrl">The address to pre-fill; empty when there is none to offer.</param>
/// <param name="IsFreshInstall">Nothing has ever been configured on this machine (no address AND no stored credentials).</param>
public sealed record StartupPlan(StartupDestination Destination, string ServerUrl, bool IsFreshInstall);

/// <summary>
/// The one place that decides where a start-up lands. It exists as a pure function of three facts so the decision can be
/// tested — the windows themselves cannot be, off Windows — and so that the answer cannot drift between
/// <c>AuthFlow.RunStartupAsync</c> and whatever else asks the same question later.
/// <para>
/// The rule that matters: <b>an app that does not know its server address never opens the sign-in window.</b> Until week 6
/// it did, with an empty address field, which asks the user for a URL they were never told in a form that does not explain
/// it. A missing address sends the start-up through the guided run instead, whether it is a brand-new machine or an
/// install whose settings were lost — in both cases the address is the first thing needed and the last thing the user can
/// guess.
/// </para>
/// </summary>
public static class StartupPlanner
{
    /// <param name="settings">The persisted settings (an empty <see cref="AppSettings.ServerUrl"/> is the fresh state).</param>
    /// <param name="credentialsStored">A refresh token exists in the secret store, even if it is no longer valid.</param>
    /// <param name="sessionRestored">The silent sign-in succeeded, i.e. there is a live session already.</param>
    public static StartupPlan Decide(AppSettings? settings, bool credentialsStored, bool sessionRestored)
    {
        var url = settings?.ServerUrl ?? string.Empty;
        var usable = HttpServerCheck.Validate(url) is { IsOk: true } validated ? validated.NormalizedUrl! : null;

        if (sessionRestored)
        {
            return new StartupPlan(StartupDestination.MainWindow, usable ?? url.Trim(), IsFreshInstall: false);
        }

        if (usable is null)
        {
            // No address: the sign-in window has nothing to sign in to. "Fresh" only decides the wording of the welcome,
            // not the route — an install that lost its settings needs exactly the same first step.
            return new StartupPlan(StartupDestination.FirstRun, string.Empty, IsFreshInstall: !credentialsStored);
        }

        return new StartupPlan(StartupDestination.SignIn, usable, IsFreshInstall: false);
    }
}
