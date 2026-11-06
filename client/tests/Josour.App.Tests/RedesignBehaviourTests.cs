using System.Reflection;
using Avalonia;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Logging.Abstractions;
using Josour.App.Models;
using Josour.App.Services;
using Josour.App.Services.Notifications;
using Josour.App.Theme;
using Josour.App.ViewModels;
using Josour.Core.Control;
using Josour.Infrastructure.Api;
using Josour.Infrastructure.Contacts;
using Josour.Infrastructure.Diagnostics;
using Josour.Infrastructure.Localization;
using Josour.Infrastructure.Session;
using Josour.Infrastructure.Settings;
using Josour.Infrastructure.SystemProxy;

namespace Josour.App.Tests;

/// <summary>
/// The view-model additions of the desktop redesign (docs/design/desktop-phase2-mockups.html, "View-model additions"): only
/// what the screens need to say, computed from facts the app already had. Nothing here touches the network.
/// </summary>
public class RedesignBehaviourTests
{
    // ------------------------------------------------------------------------------------------------ the hero's status

    [Fact]
    public void The_hero_says_off_until_sharing_is_turned_on()
    {
        var (host, _) = Host();

        Assert.Equal(HostStatus.Off, host.Status);
    }

    [Fact]
    public void A_short_drop_reads_as_getting_ready_and_only_a_long_one_as_unreachable()
    {
        var (host, _) = Host();
        host.IsAvailable = true;
        host.IsConnected = false;

        // Inside the grace a reconnect is normal: no alarm.
        Assert.Equal(HostStatus.Connecting, host.Status);

        host.UnreachableGrace = TimeSpan.Zero;
        Assert.Equal(HostStatus.Unreachable, host.ComputeStatus());
    }

    [Fact]
    public void Ready_needs_the_server_to_have_taken_the_announcement()
    {
        var (host, _) = Host();
        host.IsAvailable = true;
        host.IsConnected = true;
        Assert.Equal(HostStatus.Connecting, host.Status);

        host.IsAnnounced = true;
        Assert.Equal(HostStatus.Ready, host.Status);
    }

    [Fact]
    public void The_warnings_are_ranked_and_the_hero_s_own_is_not_repeated_as_a_row()
    {
        var (host, _) = Host();
        host.IsAvailable = true;
        host.IsConnected = true;
        host.IsAnnounced = true;
        host.IsFirewallRuleMissing = true;
        host.IsVpnWarningVisible = true;
        host.VpnWarningMessage = "vpn";

        // The firewall is the hero's subject, so the only row is the VPN.
        Assert.Equal(HostStatus.NeedsAttention, host.Status);
        Assert.Equal(new[] { HostNoticeKind.Vpn }, host.Notices.Select(n => n.Kind));

        // Once the server is out of reach, that is the hero's, and the firewall drops to the first row.
        host.UnreachableGrace = TimeSpan.Zero;
        host.IsConnected = false;
        Assert.Equal(HostStatus.Unreachable, host.Status);
        Assert.Equal(new[] { HostNoticeKind.Firewall, HostNoticeKind.Vpn }, host.Notices.Select(n => n.Kind));
    }

    // ------------------------------------------------------------------------------------------------ the request window

    private static IncomingRequestViewModel Request(AllowlistDisclosure? scope = null) => new(
        new IncomingRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Sara", "SARA-LAPTOP", 30, DateTimeOffset.UtcNow.AddSeconds(60),
            scope ?? AllowlistDisclosure.NoRestriction(3)),
        NullLogger<IncomingRequestViewModel>.Instance);

    [AvaloniaFact]
    public async Task An_expired_request_says_so_and_shares_nothing()
    {
        using var vm = Request();

        vm.Expire();

        Assert.True(vm.IsExpired);
        Assert.Contains("Sara", vm.ExpiredText, StringComparison.Ordinal);
        var answer = await vm.Completion;
        Assert.Equal(IncomingRequestDecision.TimedOut, answer.Decision);
        Assert.False(answer.AlwaysAllow);
    }

    [AvaloniaFact]
    public void Accept_names_the_standing_consent_while_it_is_ticked()
    {
        using var vm = Request();
        Assert.Equal(Strings.Accept, vm.AcceptText);

        vm.TrustThisGuest = true;

        Assert.Equal(Strings.DesktopAcceptAndTrust, vm.AcceptText);
    }

    [AvaloniaFact]
    public void Any_site_is_said_plainly_and_a_list_that_failed_is_a_warning()
    {
        using var unrestricted = Request();
        Assert.True(unrestricted.IsScopePlain);
        Assert.False(unrestricted.IsScopeWarning);
        Assert.Equal(Strings.MobileScopeUnrestricted, unrestricted.AllowedSitesMessage);

        using var failed = Request(AllowlistDisclosure.Unavailable(3));
        Assert.True(failed.IsScopeWarning);
        Assert.False(failed.IsScopePlain);
    }

    // ------------------------------------------------------------------------------------------------ sign-in

    [Fact]
    public void The_sign_in_window_shows_why_the_server_signed_this_device_out()
    {
        var flow = new FakeFlow { SignOutNotice = Strings.SignedOutDeviceRevoked };

        var vm = new LoginViewModel(new NoApi(), new StubAuthSession(null), new FixedSettings(), flow, NullLogger<LoginViewModel>.Instance);

        Assert.True(vm.HasNotice);
        Assert.Equal(Strings.SignedOutDeviceRevoked, vm.NoticeText);
    }

    [Fact]
    public void The_password_can_be_shown_and_its_button_says_what_it_does()
    {
        var vm = new LoginViewModel(new NoApi(), new StubAuthSession(null), new FixedSettings(), new FakeFlow(), NullLogger<LoginViewModel>.Instance);
        Assert.NotEqual('\0', vm.PasswordChar);
        Assert.Equal(Strings.MobileShowPassword, vm.RevealPasswordName);

        vm.TogglePasswordCommand.Execute(null);

        Assert.Equal('\0', vm.PasswordChar);
        Assert.Equal(Strings.DesktopHidePassword, vm.RevealPasswordName);
    }

    // ------------------------------------------------------------------------------------------------ theme and icons

    [AvaloniaFact]
    public void Arabic_gets_taller_lines_and_captions_never_below_13()
    {
        var resources = Application.Current!.Resources;
        try
        {
            DesktopTheme.Apply(resources, arabic: true);
            Assert.Equal(13d, resources["Josour.FontSize.Caption"]);
            Assert.Equal(24d, resources["Josour.LineHeight.Body"]);

            DesktopTheme.Apply(resources, arabic: false);
            Assert.Equal(12d, resources["Josour.FontSize.Caption"]);
            Assert.Equal(20d, resources["Josour.LineHeight.Body"]);
            Assert.Equal(14d, resources["Josour.FontSize.Body"]);
        }
        finally
        {
            DesktopTheme.Apply(resources, LocalizedStrings.Current.IsRightToLeft);
        }
    }

    [Theory]
    [InlineData(TrayIconState.Available, false, true, "avares://Josour/Assets/Tray/available-light.ico")]
    [InlineData(TrayIconState.InSession, false, false, "avares://Josour/Assets/Tray/session-dark.ico")]
    [InlineData(TrayIconState.Unreachable, true, false, "avares://Josour/Assets/Tray/unreachable-template.png")]
    [InlineData(TrayIconState.Off, true, true, "avares://Josour/Assets/Tray/off-template.png")]
    public void Each_tray_state_has_its_own_icon_for_each_system(TrayIconState state, bool mac, bool lightTaskbar, string expected)
    {
        Assert.Equal(expected, TrayService.IconUri(state, mac, lightTaskbar));
    }

    [AvaloniaFact]
    public void Every_tray_icon_ships_in_the_app()
    {
        foreach (var state in Enum.GetValues<TrayIconState>())
        {
            foreach (var (mac, light) in new[] { (false, true), (false, false), (true, false) })
            {
                Assert.True(Avalonia.Platform.AssetLoader.Exists(new Uri(TrayService.IconUri(state, mac, light))), $"{state} mac={mac} light={light}");
            }
        }
    }

    // ------------------------------------------------------------------------------------------------ fakes

    private static (HostViewModel Host, FakeChannel Channel) Host()
    {
        var channel = new FakeChannel();
        var sessions = new SessionCoordinator(channel, new NoApi(), Fake.Of<ITunnelSessionFactory>(), Fake.Of<ISystemProxyGuard>());
        var host = new HostViewModel(
            channel,
            sessions,
            Fake.Of<IIncomingRequestPresenter>(),
            Fake.Of<IAllowlistDisclosure>(),
            new HostReadinessMonitor(new NoProbe(), NullLogger<HostReadinessMonitor>.Instance),
            new StubAuthSession(null),
            Fake.Of<IContactsStore>(),
            new NoApi(),
            Fake.Of<INotifier>(),
            NullLogger<HostViewModel>.Instance);
        return (host, channel);
    }

    private sealed class NoApi : StubApiClient
    {
    }

    private sealed class NoProbe : IHostReadinessProbe
    {
        public Task<HostReadiness> InspectAsync(CancellationToken ct) => Task.FromResult(HostReadiness.Unknown);
    }

    private sealed class FakeFlow : IAuthFlow
    {
        public string? SignOutNotice { get; set; }
        public Task RunStartupAsync(CancellationToken ct) => Task.CompletedTask;
        public Task CompleteSignInAsync(CancellationToken ct) => Task.CompletedTask;
        public Task SignOutAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FixedSettings : IAppSettingsStore
    {
        public AppSettings Current { get; } = new() { ServerUrl = "https://josour.example.org" };
        public event EventHandler<AppSettings>? Changed { add { } remove { } }
        public Task SaveAsync(AppSettings settings, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeChannel : IControlChannel
    {
        public ControlChannelState State { get; set; } = ControlChannelState.Disconnected;
        public event Action<ControlChannelState>? StateChanged { add { } remove { } }
        public event Action<ControlMessage>? MessageReceived { add { } remove { } }
        public event Action<ControlChannelClosed>? Closed { add { } remove { } }
        public Task<HelloAckMessage> ConnectAsync(string accessToken, Guid deviceId, Dictionary<string, object?>? diagnostics, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task SendAsync(ControlMessage message, CancellationToken ct) => Task.CompletedTask;
        public Task<ControlMessage> RequestAsync(ControlMessage message, TimeSpan timeout, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>An interface whose every member does nothing and returns a default (a completed task for async ones).</summary>
public class Fake : DispatchProxy
{
    public static T Of<T>() where T : class => Create<T, Fake>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var type = targetMethod?.ReturnType;
        if (type is null || type == typeof(void))
        {
            return null;
        }

        if (type == typeof(Task))
        {
            return Task.CompletedTask;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var result = type.GetGenericArguments()[0];
            var value = result.IsValueType ? Activator.CreateInstance(result) : null;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(result).Invoke(null, new[] { value });
        }

        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
