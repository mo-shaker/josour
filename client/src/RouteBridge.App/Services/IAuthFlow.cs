namespace RouteBridge.App.Services;

/// <summary>Start-up and sign-in/sign-out orchestration: which window to show, when to open/close the control channel.</summary>
public interface IAuthFlow
{
    /// <summary>Silent restore → MainWindow (or tray only with <c>--minimized</c>); otherwise the LoginWindow.</summary>
    Task RunStartupAsync(CancellationToken ct);

    /// <summary>Called by the sign-in window after <c>IAuthSession.SignInAsync</c> succeeded: connect, close login, show main.</summary>
    Task CompleteSignInAsync(CancellationToken ct);

    /// <summary>Tray "Sign out": close the channel, sign out, show the sign-in window.</summary>
    Task SignOutAsync(CancellationToken ct);
}
