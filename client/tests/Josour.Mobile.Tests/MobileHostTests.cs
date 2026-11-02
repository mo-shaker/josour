using System.Net;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Josour.Core.Allowlist;
using Josour.Core.Control;
using Josour.Core.Tunnel;
using Josour.Infrastructure.Session;
using Josour.Mobile.Platform;
using Josour.Mobile.Services;
using Josour.Mobile.ViewModels;
using Josour.Mobile.Views;
using Microsoft.Extensions.Logging.Abstractions;

namespace Josour.Mobile.Tests;

public class MobileHostTests
{
    [Fact]
    public void The_phone_never_declares_the_guest_capabilities()
    {
        Assert.Contains(ClientCapabilities.Contacts, MobileRuntime.Capabilities);
        Assert.Contains(ClientCapabilities.TrustV2, MobileRuntime.Capabilities);
        Assert.DoesNotContain(ClientCapabilities.Offers, MobileRuntime.Capabilities);
        Assert.DoesNotContain(ClientCapabilities.SystemProxy, MobileRuntime.Capabilities);
    }

    [Fact]
    public void The_host_tunnel_blocks_the_phones_networks_and_runs_no_upnp_and_no_guest_proxy()
    {
        var gateway = IPAddress.Parse("192.168.1.1");
        var onLink = new IPNetwork(IPAddress.Parse("2a00:1450:4001::"), 64);
        var request = new TunnelSessionRequest(
            new SessionMaterial(Guid.NewGuid(), TunnelRole.Host, new byte[32], DateTimeOffset.UtcNow.AddMinutes(30), false, "198.51.100.7"),
            DenyAllAllowlist.Instance,
            new[] { 80, 443 },
            "203.0.113.5",
            null);

        var options = MobileTunnelSessionFactory.BuildOptions(request, new[] { gateway }, new[] { onLink });

        Assert.False(options.EnableUpnp);
        Assert.False(options.StartGuestProxy);
        Assert.NotNull(options.HostEgress);
        Assert.Contains(gateway, options.LocalAddresses!);
        Assert.Contains(onLink, options.LocalNetworks!);
        Assert.Equal("203.0.113.5", options.OurPublicIp);
    }

    [Fact]
    public void The_phone_refuses_to_be_a_guest()
    {
        var factory = new MobileTunnelSessionFactory(new FakeNetwork(), NullLogger<MobileTunnelSessionFactory>.Instance);
        var request = new TunnelSessionRequest(
            new SessionMaterial(Guid.NewGuid(), TunnelRole.Guest, new byte[32], DateTimeOffset.UtcNow.AddMinutes(30), false, "198.51.100.7"),
            DenyAllAllowlist.Instance,
            new[] { 80, 443 },
            null,
            null);

        Assert.Throws<NotSupportedException>(() => factory.Create(request));
    }

    [Fact]
    public void The_blocked_lists_are_the_union_without_duplicates()
    {
        var a = IPAddress.Parse("10.0.0.1");
        var b = IPAddress.Parse("192.168.1.1");
        Assert.Equal(new[] { a, b }, MobileTunnelSessionFactory.Union(new[] { a }, new[] { b, a }));
    }

    [AvaloniaFact]
    public void The_main_view_loads_signed_out()
    {
        using var phone = new TestPhone();
        var view = new MainView { DataContext = phone.Get<MainViewModel>() };
        var window = new Avalonia.Controls.Window { Content = view, Width = 400, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Being_available_runs_the_foreground_service_and_turning_it_off_stops_it()
    {
        using var phone = new TestPhone();
        var host = phone.Get<HostViewModel>();

        host.IsAvailable = true;
        Assert.True(phone.Hosting.Running);
        Assert.False(phone.Hosting.LastSessionActive);

        host.IsAvailable = false;
        Assert.False(phone.Hosting.Running);
    }

    [AvaloniaFact]
    public void Mobile_data_is_named_and_warns_about_the_data_plan()
    {
        using var phone = new TestPhone();
        var host = phone.Get<HostViewModel>();
        Assert.False(host.IsCellular);

        phone.Network.Move(new NetworkSnapshot(NetworkKind.Cellular, false, Array.Empty<IPAddress>(), Array.Empty<IPNetwork>()));
        Dispatcher.UIThread.RunJobs();

        Assert.True(host.IsCellular);
        Assert.Equal(Tr.Get(Infrastructure.Localization.UiStringKeys.MobileNetworkCellular), host.NetworkText);
    }

    [AvaloniaFact]
    public async Task An_incoming_request_is_shown_with_a_notification_and_a_rejection_clears_both()
    {
        using var phone = new TestPhone();
        var host = phone.Get<HostViewModel>();
        phone.Get<SessionCoordinator>();
        await phone.Channel.ConnectAsync("token", Guid.NewGuid(), null, CancellationToken.None);
        host.IsAvailable = true;

        var requestId = phone.Channel.SimulateIncomingRequest("سارة", "SARA-LAPTOP", 30);
        Assert.NotNull(requestId);
        await WaitUntil(() => host.Incoming is not null);

        Assert.Equal(requestId, host.Incoming!.RequestId);
        Assert.Contains("request:" + requestId, phone.Notifier.Events);

        host.Incoming.RejectCommand.Execute(null);
        await WaitUntil(() => host.Incoming is null);
        Assert.Contains("cancel:" + requestId, phone.Notifier.Events);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition did not become true.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }
}
