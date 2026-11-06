using System.Windows.Input;
using Josour.App.ViewModels;
using Josour.Brand.Controls;

namespace Josour.App.Tests;

// Plain stand-ins for the view models, with the property names the windows bind to (compiled bindings are off, so any object
// with those names renders). They let the rendering tests draw every state without a server, a session or the app's data.

public sealed class NoCommand : ICommand
{
    public static readonly NoCommand Instance = new();
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) { }
}

public sealed class SessionFake
{
    public string PeerText { get; set; } = "";
    public string PhaseText { get; set; } = Strings.SessionPhaseActive;
    public bool HasCountdown { get; set; } = true;
    public string TimeLeftText { get; set; } = UiFlow.Ltr("41:12");
    public string DataUsedText { get; set; } = "";
    public bool IsSameNetworkGuest { get; set; }
    public bool IsGuestActive { get; set; }
    public bool HasNoticeMessage { get; set; }
    public string NoticeMessage { get; set; } = "";
    public bool CanOpenCheckPage { get; set; }
    public bool CanDisconnect { get; set; } = true;
    public ICommand OpenCheckPageCommand => NoCommand.Instance;
    public ICommand DisconnectCommand => NoCommand.Instance;
    public ICommand DismissCommand => NoCommand.Instance;
}

public sealed class HostFake
{
    public bool IsAvailable { get; set; } = true;
    public List<HostNotice> Notices { get; set; } = new();
}

public sealed class InviteFake
{
    public string Initial { get; set; } = "";
    public string Text { get; set; } = "";
    public string EmailText { get; set; } = "";
    public ICommand AcceptCommand => NoCommand.Instance;
    public ICommand DeclineCommand => NoCommand.Instance;
}

public sealed class ContactFake
{
    public string Initial { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string EmailText { get; set; } = "";
    public bool IsOnline { get; set; }
    public string PresenceText => IsOnline ? Strings.ContactOnline : Strings.ContactOffline;
    public bool IsOnSameNetwork { get; set; }
    public bool AllowWithoutAsking { get; set; }
    public string TrustHelpText => AllowWithoutAsking ? Strings.MobileTrustOnHelp : Strings.MobileTrustOffHelp;
    public string TrustAccessibleName => Strings.ContactAllowAlways;
    public bool TrustsMe { get; set; }
    public bool IsConfirmingRemove { get; set; }
    public string RemoveConfirmText => string.Format(UiFlow.Culture, Strings.MobileRemoveConfirmFormat, DisplayName);
    public ICommand UseConnectionCommand => NoCommand.Instance;
    public ICommand OfferConnectionCommand => NoCommand.Instance;
    public ICommand RemoveCommand => NoCommand.Instance;
    public ICommand CancelRemoveCommand => NoCommand.Instance;
}

public sealed class OutgoingFake
{
    public string EmailText { get; set; } = "";
    public string StatusText { get; set; } = Strings.ContactsOutgoingPending;
    public ICommand WithdrawCommand => NoCommand.Instance;
}

public sealed class ContactsFake
{
    public List<int> DurationOptions { get; } = new() { 15, 30, 60 };
    public int SelectedDuration { get; set; } = 30;
    public string StatusMessage { get; set; } = "";
    public bool StatusIsError { get; set; }
    public bool HasErrorMessage => StatusMessage.Length > 0 && StatusIsError;
    public bool HasInfoMessage => StatusMessage.Length > 0 && !StatusIsError;
    public bool IsWaiting { get; set; }
    public string WaitingText { get; set; } = "";
    public bool HasPendingOffer { get; set; }
    public string PendingOfferText { get; set; } = "";
    public List<InviteFake> IncomingInvites { get; set; } = new();
    public bool HasIncomingInvites => IncomingInvites.Count > 0;
    public List<ContactFake> Contacts { get; set; } = new();
    public bool HasContacts => Contacts.Count > 0;
    public bool IsLoading { get; set; }
    public bool ShowsEmpty => !HasContacts && !IsLoading;
    public List<OutgoingFake> OutgoingInvites { get; set; } = new();
    public bool HasOutgoingInvites => OutgoingInvites.Count > 0;
    public string InviteEmail { get; set; } = "";
    public bool IsConfirmingTrust { get; set; }
    public string TrustConfirmTitle { get; set; } = "";
    public ICommand CancelWaitingCommand => NoCommand.Instance;
    public ICommand AcceptOfferCommand => NoCommand.Instance;
    public ICommand DeclineOfferCommand => NoCommand.Instance;
    public ICommand InviteCommand => NoCommand.Instance;
    public ICommand ConfirmTrustCommand => NoCommand.Instance;
    public ICommand CancelTrustCommand => NoCommand.Instance;
}

public sealed class MainFake
{
    public HeroState Hero { get; set; } = HeroState.Ready;
    public bool IsHeroSession => Hero is HeroState.HostSession or HeroState.GuestSession;
    public bool IsHeroEnded => Hero == HeroState.SessionEnded;
    public bool IsHeroProblem => Hero is HeroState.Unreachable or HeroState.NeedsAttention;
    public bool CanReconnect => Hero == HeroState.Unreachable;
    public bool ShowsAvailabilityInHero => !IsHeroSession && !IsHeroEnded;
    public string HeroTitle { get; set; } = "";
    public string HeroText { get; set; } = "";
    public MotifState HeroMotif { get; set; } = MotifState.Ready;
    public bool IsAdmin { get; set; } = true;
    public bool IsConfirmingSignOut { get; set; }
    public bool IsConfirmingQuit { get; set; }
    public string ConfirmText { get; set; } = "";
    public string AutoNoteText { get; set; } = "";
    public bool HasAutoNote => AutoNoteText.Length > 0;
    public string ServerStatusText { get; set; } = string.Format(UiFlow.Culture, Strings.MobileServerStatusFormat, Strings.StatusConnected);
    public string DeviceNameText { get; set; } = Strings.AboutDeviceNameLabel + ": " + UiFlow.Ltr("SARA-LAPTOP");
    public string AccountText { get; set; } = UiFlow.Ltr("sara@example.com");
    public HostFake Host { get; set; } = new();
    public SessionFake Session { get; set; } = new();
    public ContactsFake Contacts { get; set; } = new();
    public ICommand ShowSettingsCommand => NoCommand.Instance;
    public ICommand ShowAboutCommand => NoCommand.Instance;
    public ICommand ShowAdminCommand => NoCommand.Instance;
    public ICommand AskSignOutCommand => NoCommand.Instance;
    public ICommand SignOutCommand => NoCommand.Instance;
    public ICommand CancelSignOutCommand => NoCommand.Instance;
    public ICommand ExitCommand => NoCommand.Instance;
    public ICommand CancelQuitCommand => NoCommand.Instance;
    public ICommand ReconnectNowCommand => NoCommand.Instance;
    public ICommand AskQuitCommand => NoCommand.Instance;
}

public sealed class LoginFake
{
    public bool IsIdle { get; set; } = true;
    public bool IsBusy => !IsIdle;
    public bool IsRegistering { get; set; }
    public bool IsSigningIn => !IsRegistering;
    public string ServerUrl { get; set; } = "https://josour.example.org";
    public string DisplayName { get; set; } = "";
    public string Email { get; set; } = "sara@example.com";
    public string Password { get; set; } = "correct-horse";
    public char PasswordChar => '●';
    public string RevealPasswordName => Strings.MobileShowPassword;
    public string? NoticeText { get; set; }
    public bool HasNotice => !string.IsNullOrEmpty(NoticeText);
    public string ErrorText { get; set; } = "";
    public bool HasError => ErrorText.Length > 0;
    public bool HasInfo { get; set; }
    public string InfoText { get; set; } = "";
    public string SignInButtonText { get; set; } = Strings.LoginSignIn;
    public ICommand SignInCommand => NoCommand.Instance;
    public ICommand RegisterCommand => NoCommand.Instance;
    public ICommand ToggleRegisteringCommand => NoCommand.Instance;
    public ICommand TogglePasswordCommand => NoCommand.Instance;
}

public sealed class FirstRunFake
{
    public int Step { get; set; }
    public bool IsIdle { get; set; } = true;
    public bool IsBusy => !IsIdle;
    public bool IsServerStep => Step == 0;
    public bool IsSignInStep => Step == 1;
    public bool IsReadinessStep => Step == 2;
    public bool IsStepTwoReached => Step >= 1;
    public bool IsStepThreeReached => Step >= 2;
    public string StepText => string.Format(UiFlow.Culture, Strings.FirstRunStepFormat, Step + 1, 3);
    public string StepTitle => Step switch { 0 => Strings.FirstRunServerTitle, 1 => Strings.FirstRunSignInTitle, _ => Strings.FirstRunReadyTitle };
    public string StepDescription => Step switch { 0 => Strings.FirstRunServerText, 1 => Strings.MobileLoginDescription, _ => Strings.FirstRunReadyText };
    public string ServerUrl { get; set; } = "https://josour.example.org";
    public bool IsRegistering { get; set; }
    public bool IsSigningIn => !IsRegistering;
    public string DisplayName { get; set; } = "";
    public string Email { get; set; } = "sara@example.com";
    public string Password { get; set; } = "correct-horse";
    public char PasswordChar => '●';
    public string RevealPasswordName => Strings.MobileShowPassword;
    public bool IsFirewallWarning { get; set; } = true;
    public string FirewallText { get; set; } = Strings.ReadinessFirewallMissing;
    public bool IsVpnWarning { get; set; }
    public string VpnText { get; set; } = Strings.ReadinessVpnOk;
    public bool IsReadinessChecking { get; set; }
    public string Message { get; set; } = "";
    public bool HasMessage => Message.Length > 0;
    public bool IsMessageAnError { get; set; }
    public ICommand RecheckReadinessCommand => NoCommand.Instance;
    public ICommand BackToServerCommand => NoCommand.Instance;
    public ICommand ContinueFromServerCommand => NoCommand.Instance;
    public ICommand SignInCommand => NoCommand.Instance;
    public ICommand RegisterCommand => NoCommand.Instance;
    public ICommand FinishCommand => NoCommand.Instance;
    public ICommand TogglePasswordCommand => NoCommand.Instance;
}

public sealed class SettingsFake
{
    public bool IsIdle { get; set; } = true;
    public bool IsBusy => !IsIdle;
    public string ServerUrl { get; set; } = "https://josour.example.org";
    public string TestButtonText { get; set; } = Strings.SettingsTestConnection;
    public string ServerMessage { get; set; } = "";
    public bool HasServerMessage => ServerMessage.Length > 0;
    public bool IsServerMessageAnError { get; set; }
    public object SelectedLanguage { get; set; } = SettingsViewModel.Languages[0];
    public bool IsLanguageRestartNoteVisible { get; set; }
    public bool OpenCheckPageOnConnect { get; set; } = true;
    public bool StartAtLogin { get; set; } = true;
    public bool IsStartAtLoginSupported { get; set; } = true;
    public bool StartMinimized { get; set; }
    public string SaveMessage { get; set; } = "";
    public bool HasSaveMessage => SaveMessage.Length > 0;
    public ICommand TestConnectionCommand => NoCommand.Instance;
    public ICommand CancelCommand => NoCommand.Instance;
    public ICommand SaveCommand => NoCommand.Instance;
}

public sealed class AboutFake
{
    public string AppVersion { get; set; } = UiFlow.Ltr("2.7.0");
    public string DeviceName { get; set; } = UiFlow.Ltr("SARA-LAPTOP");
    public string DeviceId { get; set; } = UiFlow.Ltr("3f2c9a1e-7b44-4d0e-9a51-2c8e6f1d0b7a");
    public string OperatingSystem { get; set; } = UiFlow.Ltr("macOS 15.1 (24B83)");
    public string ServerUrl { get; set; } = UiFlow.Ltr("https://josour.example.org");
    public string SignedInAs { get; set; } = UiFlow.Ltr("sara@example.com");
    public string ConnectionState { get; set; } = Strings.StatusConnected;
    public string LogDirectory { get; set; } = UiFlow.Ltr("~/Library/Application Support/Josour/logs");
    public string Message { get; set; } = "";
    public bool HasMessage => Message.Length > 0;
    public bool IsMessageAnError { get; set; }
    public ICommand CopyDetailsCommand => NoCommand.Instance;
    public ICommand OpenLogFolderCommand => NoCommand.Instance;
}

public sealed class UserFake
{
    public string Initial { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Email { get; set; } = "";
    public bool IsAdmin { get; set; }
    public string StateText { get; set; } = Strings.AdminStateActive;
    public bool IsStateActive { get; set; } = true;
    public bool IsStateLocked { get; set; }
    public bool IsStateDisabled { get; set; }
    public bool CanUnlock { get; set; }
    public string ToggleActiveText { get; set; } = Strings.AdminDisable;
    public bool CanToggleActive { get; set; } = true;
}

public sealed class AdminFake
{
    public string SearchText { get; set; } = "";
    public List<UserFake> Users { get; set; } = new();
    public bool HasUsers => Users.Count > 0;
    public bool IsLoading { get; set; }
    public bool ShowsEmpty => !HasUsers && !IsLoading;
    public bool IsBusy { get; set; }
    public string Message { get; set; } = "";
    public bool HasMessage => Message.Length > 0;
    public bool IsMessageAnError { get; set; }
    public bool IsConfirmingDisable { get; set; }
    public string DisableConfirmTitle { get; set; } = "";
    public string NewEmail { get; set; } = "";
    public string NewDisplayName { get; set; } = "";
    public string NewPassword { get; set; } = "";
    public bool NewIsAdmin { get; set; }
    public ICommand RefreshCommand => NoCommand.Instance;
    public ICommand UnlockCommand => NoCommand.Instance;
    public ICommand ToggleActiveCommand => NoCommand.Instance;
    public ICommand CreateUserCommand => NoCommand.Instance;
    public ICommand OpenWebDashboardCommand => NoCommand.Instance;
    public ICommand ConfirmDisableCommand => NoCommand.Instance;
    public ICommand CancelDisableCommand => NoCommand.Instance;
}
