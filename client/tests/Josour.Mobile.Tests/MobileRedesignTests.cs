using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Josour.Infrastructure.Api;
using Josour.Infrastructure.Localization;
using Josour.Infrastructure.Session;
using Josour.Mobile.Platform;
using Josour.Mobile.ViewModels;
using Josour.Mobile.Views;
using Microsoft.Extensions.Logging.Abstractions;

namespace Josour.Mobile.Tests;

/// <summary>
/// The redesign around the qantara mark (docs/design/mobile-phase2-mockups.html): one status for the Share tab's hero, an
/// expired request that says so, standing consent that asks first, a sign-out that names what stops, and every screen
/// drawing in both languages and both themes.
/// </summary>
public class MobileRedesignTests
{
    private static readonly NetworkSnapshot Wifi = new(NetworkKind.WiFi, false, Array.Empty<IPAddress>(), Array.Empty<IPNetwork>());
    private static readonly NetworkSnapshot NoNetwork = new(NetworkKind.None, false, Array.Empty<IPAddress>(), Array.Empty<IPNetwork>());

    [AvaloniaFact]
    public async Task The_hero_follows_the_order_of_precedence()
    {
        using var phone = new TestPhone();
        var host = phone.Get<HostViewModel>();
        Assert.Equal(HostStatus.Off, host.Status);
        Assert.Equal(Tr.Get(UiStringKeys.MobileStatusOffTitle), host.HeroTitle);

        host.UnreachableGrace = TimeSpan.FromMinutes(1);
        host.IsAvailable = true;
        Assert.Equal(HostStatus.Connecting, host.Status); // a moment without the server is not yet a failure

        host.UnreachableGrace = TimeSpan.Zero;
        phone.Network.Move(Wifi);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(HostStatus.Unreachable, host.Status);
        Assert.True(host.IsStatusProblem);

        phone.Network.Move(NoNetwork);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(HostStatus.NoNetwork, host.Status);

        phone.Network.Move(Wifi);
        await phone.Channel.ConnectAsync("token", Guid.NewGuid(), null, CancellationToken.None);
        await WaitUntil(() => host.IsConnected);
        Assert.Equal(HostStatus.Connecting, host.Status); // connected, not yet confirmed by the server

        host.IsAnnounced = true;
        Assert.Equal(HostStatus.Ready, host.Status);
        Assert.Equal(MotifState.Ready, host.HeroMotif);

        phone.Permissions.NeedsNotificationPermission = true;
        phone.Permissions.Raise();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(HostStatus.NeedsNotifications, host.Status);
        Assert.Equal(Tr.Get(UiStringKeys.MobileNotificationsOffWarning), host.HeroText);

        phone.Permissions.NeedsNotificationPermission = false;
        phone.Permissions.IsBatteryOptimized = true;
        phone.Permissions.Raise();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(HostStatus.NeedsBackground, host.Status);

        host.IsAvailable = false;
        Assert.Equal(HostStatus.Off, host.Status);
        Assert.True(host.ShowBeforeYouStart); // off, with a permission still missing
    }

    [AvaloniaFact]
    public async Task An_expired_request_stays_on_screen_until_it_is_closed()
    {
        using var phone = new TestPhone();
        var host = phone.Get<HostViewModel>();
        phone.Get<SessionCoordinator>();
        host.ExpiredHold = TimeSpan.FromMinutes(1);
        await phone.Channel.ConnectAsync("token", Guid.NewGuid(), null, CancellationToken.None);
        host.IsAvailable = true;

        var requestId = phone.Channel.SimulateIncomingRequest("سارة", "SARA-LAPTOP", 30);
        await WaitUntil(() => host.Incoming is not null);
        var prompt = host.Incoming!;

        prompt.Expire();
        await WaitUntil(() => prompt.IsExpired);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(prompt, host.Incoming);
        Assert.False(prompt.AcceptCommand.CanExecute(null));
        Assert.Contains("سارة", prompt.ExpiredText);
        Assert.Contains("cancel:" + requestId, phone.Notifier.Events);

        prompt.CloseCommand.Execute(null);
        await WaitUntil(() => host.Incoming is null);
    }

    [AvaloniaFact]
    public async Task Allowing_without_asking_waits_for_the_confirmation_and_turning_it_off_does_not()
    {
        var sara = new ContactDto(Guid.NewGuid(), "Sara", "sara@example.com", true, false, false, DateTimeOffset.UtcNow);
        var store = new FakeContactsStore { ContactsList = [sara] };
        var page = new ContactsViewModel(FakeApi.Create(out var calls), store, new FakeNotifier(), NullLogger<ContactsViewModel>.Instance);
        var row = page.Contacts.Single();

        row.AllowWithoutAsking = true;
        Assert.Empty(calls);
        Assert.Same(row, page.TrustCandidate);
        Assert.Contains("Sara", row.TrustConfirmTitle);

        row.CancelTrustCommand.Execute(null);
        Assert.False(row.AllowWithoutAsking);
        Assert.False(page.HasTrustCandidate);
        Assert.Empty(calls);

        row.AllowWithoutAsking = true;
        await row.ConfirmTrustCommand.ExecuteAsync(null);
        Assert.Contains($"SetTrustAsync:{sara.UserId}", calls);

        var after = page.Contacts.Single(); // the lists were read again
        calls.Clear();
        after.AllowWithoutAsking = true;
        after.CancelTrustCommand.Execute(null);
        Assert.Empty(calls);
    }

    [AvaloniaFact]
    public async Task Contacts_show_placeholders_until_the_first_list_and_put_invite_errors_by_the_invite()
    {
        var store = new FakeContactsStore();
        var page = new ContactsViewModel(
            FakeApi.Create(out _, fail: new ApiException(HttpStatusCode.NotFound, "not_found", "No such user")),
            store, new FakeNotifier(), NullLogger<ContactsViewModel>.Instance)
        {
            InviteEmail = "nobody@example.com",
        };
        Assert.True(page.IsLoading);
        Assert.False(page.ShowsEmpty);

        page.StartAnnouncing();
        Assert.True(page.ShowsEmpty);

        await page.InviteCommand.ExecuteAsync(null);
        Assert.True(page.HasInviteError);
        Assert.False(page.HasPageError);
    }

    [AvaloniaFact]
    public void Signing_out_asks_first_and_says_what_stops()
    {
        using var phone = new TestPhone();
        var main = phone.Get<MainViewModel>();

        main.AskSignOutCommand.Execute(null);
        Assert.True(main.IsConfirmingSignOut);
        Assert.Equal(Tr.Get(UiStringKeys.MobileSignOutConfirmText), main.SignOutConfirmText);

        main.CancelSignOutCommand.Execute(null);
        Assert.False(main.IsConfirmingSignOut);
    }

    [AvaloniaFact]
    public void Arabic_gets_taller_lines_and_the_font_size_setting_scales_text_as_Android_does()
    {
        var english = new ResourceDictionary();
        var arabic = new ResourceDictionary();
        MobileTheme.ApplyTypography(english, arabic: false, fontScale: 1);
        MobileTheme.ApplyTypography(arabic, arabic: true, fontScale: 2);

        Assert.Equal(16d, english["Josour.FontSize.Body"]);
        Assert.Equal(24d, english["Josour.LineHeight.Body"]);
        // Android 14+ at 200%: small text doubles, larger text grows less (16 → 30, 28 → 43.3).
        Assert.Equal(30d, arabic["Josour.FontSize.Body"]);
        Assert.Equal(52.5d, arabic["Josour.LineHeight.Body"]);
        Assert.Equal(26d, arabic["Josour.FontSize.Micro"]); // 13 sp in Arabic, never below
        Assert.Equal(43.3d, arabic["Josour.FontSize.Display"]);
        Assert.Equal(new Thickness(0, 0), english["Josour.TextInset.Body"]);
        Assert.Equal(new Thickness(9, 0), arabic["Josour.TextInset.Body"]); // room for the space RTL wrapping counts
    }

    /// <summary>
    /// Every screen draws in Arabic and English, light and dark, without throwing. With <c>JOSOUR_MOBILE_CAPTURE</c> set to a
    /// folder (never the app's own data), each frame is also saved there as a PNG to compare with the mockups.
    /// </summary>
    [AvaloniaFact]
    public async Task Every_screen_draws_in_both_languages_and_both_themes()
    {
        var capture = Environment.GetEnvironmentVariable("JOSOUR_MOBILE_CAPTURE");
        try
        {
            foreach (var language in new[] { UiLanguage.Arabic, UiLanguage.English })
            {
                foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    MobileLanguage.Apply(language);
                    Application.Current!.RequestedThemeVariant = theme;
                    foreach (var (name, arrange) in Screens())
                    {
                        // The phone's largest font setting doubles the text, not the layout: light screens are drawn at both.
                        foreach (var scale in theme == ThemeVariant.Light ? new[] { 1d, 2d } : new[] { 1d })
                        {
                        MobileTheme.SetFontScale(scale);
                        using var phone = new TestPhone();
                        var main = phone.Get<MainViewModel>();
                        await arrange(phone, main);
                        var window = new Window { Content = new MainView { DataContext = main }, Width = 360, Height = 800 };
                        window.Show();
                        Dispatcher.UIThread.RunJobs();
                        var frame = window.CaptureRenderedFrame();
                        Assert.NotNull(frame);
                        if (!string.IsNullOrEmpty(capture))
                        {
                            Directory.CreateDirectory(capture);
                            frame!.Save(Path.Combine(capture, $"{name}-{language.ToCode()}-{theme}{(scale > 1 ? "-font200" : "")}.png".ToLowerInvariant()));
                        }

                        window.Close();
                        }
                    }
                }
            }
        }
        finally
        {
            MobileLanguage.Apply(MobileLanguage.Default);
            MobileTheme.SetFontScale(1);
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        }
    }

    private static IEnumerable<(string Name, Func<TestPhone, MainViewModel, Task> Arrange)> Screens()
    {
        yield return ("starting", (_, _) => Task.CompletedTask);
        yield return ("signin", (_, main) => { main.IsStarting = false; return Task.CompletedTask; });
        yield return ("signin-error", (_, main) =>
        {
            main.IsStarting = false;
            main.SignIn.ErrorText = Tr.Get(UiStringKeys.LoginErrorInvalidCredentials);
            return Task.CompletedTask;
        });
        yield return ("share-off", (phone, main) =>
        {
            phone.Permissions.NeedsNotificationPermission = true;
            phone.Permissions.IsBatteryOptimized = true;
            phone.Permissions.Raise();
            SignedIn(main);
            return Task.CompletedTask;
        });
        yield return ("share-ready", async (phone, main) =>
        {
            SignedIn(main);
            await phone.Channel.ConnectAsync("token", Guid.NewGuid(), null, CancellationToken.None);
            main.Host.IsAvailable = true;
            await WaitUntil(() => main.Host.IsConnected);
            main.Host.IsAnnounced = true;
        });
        yield return ("share-unreachable", (_, main) =>
        {
            SignedIn(main);
            main.Host.UnreachableGrace = TimeSpan.Zero;
            main.Host.IsAvailable = true;
            return Task.CompletedTask;
        });
        yield return ("contacts-empty", (_, main) =>
        {
            SignedIn(main);
            main.Contacts.StartAnnouncing();
            main.ShowContacts();
            return Task.CompletedTask;
        });
        yield return ("request", async (phone, main) =>
        {
            SignedIn(main);
            phone.Get<SessionCoordinator>();
            await phone.Channel.ConnectAsync("token", Guid.NewGuid(), null, CancellationToken.None);
            main.Host.IsAvailable = true;
            phone.Channel.SimulateIncomingRequest("Sara", "MacBook Air", 30);
            await WaitUntil(() => main.Host.Incoming is not null);
        });
        yield return ("signout-sheet", (_, main) =>
        {
            SignedIn(main);
            main.AskSignOutCommand.Execute(null);
            return Task.CompletedTask;
        });
    }

    private static void SignedIn(MainViewModel main)
    {
        main.IsStarting = false;
        main.IsSignedIn = true;
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
