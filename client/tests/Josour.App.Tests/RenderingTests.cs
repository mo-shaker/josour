using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using Josour.App.Models;
using Josour.App.Theme;
using Josour.App.ViewModels;
using Josour.App.Views;
using Josour.Brand.Controls;
using Josour.Infrastructure.Localization;
using Josour.Infrastructure.Session;

namespace Josour.App.Tests;

/// <summary>
/// Every desktop window, in every state the mockups name (docs/design/desktop-phase2-mockups.html), drawn with Skia in Arabic
/// and English, light and dark, and at 2× (Retina, 200 % on Windows). Each frame is checked: no text is cut off or pushed past
/// the window's edge. With <c>JOSOUR_DESKTOP_CAPTURE</c> set to a folder (never the app's own data), the frames are saved
/// there as PNGs to compare with the mockups.
/// </summary>
public class RenderingTests
{
    private sealed record Scene(string Name, Func<Window> Build);

    [AvaloniaFact]
    public void Every_window_draws_in_both_languages_both_themes_and_at_2x_without_cutting_text()
    {
        var capture = Environment.GetEnvironmentVariable("JOSOUR_DESKTOP_CAPTURE");
        var problems = new List<string>();
        var checkedTexts = 0;
        try
        {
            foreach (var language in new[] { UiLanguage.Arabic, UiLanguage.English })
            {
                foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    UseLanguage(language);
                    Application.Current!.RequestedThemeVariant = theme;
                    foreach (var scene in Scenes(language))
                    {
                        var window = scene.Build();
                        window.Show();
                        Dispatcher.UIThread.RunJobs();
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                        Dispatcher.UIThread.RunJobs();

                        var frame = window.CaptureRenderedFrame();
                        Assert.NotNull(frame);
                        var name = $"{scene.Name}-{language.ToCode()}-{theme}".ToLowerInvariant();
                        problems.AddRange(CutText(window).Select(p => name + ": " + p));
                        checkedTexts += window.GetVisualDescendants().OfType<TextBlock>().Count(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text));

                        if (!string.IsNullOrEmpty(capture))
                        {
                            Directory.CreateDirectory(capture);
                            frame!.Save(Path.Combine(capture, name + ".png"));
                        }

                        // 2×: the same layout rasterised at 192 dpi, as on a Retina display or Windows at 200 %.
                        if (theme == ThemeVariant.Light)
                        {
                            var size = new PixelSize((int)(window.Bounds.Width * 2), (int)(window.Bounds.Height * 2));
                            using var twice = new RenderTargetBitmap(size, new Vector(192, 192));
                            twice.Render(window);
                            if (!string.IsNullOrEmpty(capture))
                            {
                                twice.Save(Path.Combine(capture, name + "-2x.png"));
                            }
                        }

                        window.Close();
                    }
                }
            }
        }
        finally
        {
            UseLanguage(UiLanguages.Default);
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        }

        Assert.True(checkedTexts > 2000, $"only {checkedTexts} text blocks were checked");
        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(40)));
    }

    /// <summary>The interface language for the next windows, as App.Start fixes it, with the matching type scale.</summary>
    private static void UseLanguage(UiLanguage language)
    {
        LocalizedStrings.UseLanguage(language);
        DesktopTheme.Apply(Application.Current!.Resources, LocalizedStrings.Current.IsRightToLeft);
    }

    /// <summary>
    /// Text that does not fit: a visible text block whose laid-out text is taller or wider than the space it was given (unless it
    /// trims on purpose), or that ends past the window's left or right edge.
    /// </summary>
    private static IEnumerable<string> CutText(Window window)
    {
        foreach (var text in window.GetVisualDescendants().OfType<TextBlock>())
        {
            if (!text.IsEffectivelyVisible || string.IsNullOrEmpty(text.Text) || text.Bounds.Width <= 0)
            {
                continue;
            }

            var layout = text.TextLayout;
            var trims = text.TextTrimming != Avalonia.Media.TextTrimming.None;
            if (!trims && layout.WidthIncludingTrailingWhitespace > text.Bounds.Width + 1.5 && text.TextWrapping == Avalonia.Media.TextWrapping.NoWrap)
            {
                yield return $"too wide ({layout.WidthIncludingTrailingWhitespace:0} > {text.Bounds.Width:0}): {Short(text.Text)}";
            }

            if (layout.Height > text.Bounds.Height + 1.5)
            {
                yield return $"too tall ({layout.Height:0} > {text.Bounds.Height:0}): {Short(text.Text)}";
            }

            // Inside a scroll viewer the text may be scrolled out of view vertically, but never sideways.
            var origin = text.TranslatePoint(new Point(0, 0), window);
            if (origin is { } p && (p.X < -1 || p.X + text.Bounds.Width > window.Bounds.Width + 1))
            {
                yield return $"past the window's edge (x {p.X:0}, width {text.Bounds.Width:0}): {Short(text.Text)}";
            }
        }
    }

    private static string Short(string? s) => s is null ? string.Empty : s.Length <= 40 ? s : s[..40] + "…";

    // ------------------------------------------------------------------------------------------------ scenes

    private static IEnumerable<Scene> Scenes(UiLanguage language)
    {
        var ar = language == UiLanguage.Arabic;
        string sara = ar ? "سارة أحمد" : "Sara Ahmad", omar = ar ? "عمر خالد" : "Omar Khaled", huda = ar ? "هدى سالم" : "Huda Salem", ahmad = ar ? "أحمد يوسف" : "Ahmad Yousef";

        ContactsFake Contacts() => new()
        {
            IncomingInvites = { new InviteFake { Initial = ahmad[..1], Text = string.Format(UiFlow.Culture, Strings.ContactsIncomingFormat, ahmad), EmailText = UiFlow.Ltr("ahmad@example.com") } },
            Contacts =
            {
                new ContactFake { Initial = sara[..1], DisplayName = sara, EmailText = UiFlow.Ltr("sara@example.com"), IsOnline = true, AllowWithoutAsking = true, TrustsMe = true },
                new ContactFake { Initial = omar[..1], DisplayName = omar, EmailText = UiFlow.Ltr("omar@example.com"), IsOnline = true, IsOnSameNetwork = true },
                new ContactFake { Initial = huda[..1], DisplayName = huda, EmailText = UiFlow.Ltr("huda@example.com") },
            },
            OutgoingInvites = { new OutgoingFake { EmailText = UiFlow.Ltr("mona@example.com") } },
        };

        MainFake Main(HeroState hero = HeroState.Ready) => new()
        {
            Hero = hero,
            HeroTitle = hero switch
            {
                HeroState.Off => Strings.MobileStatusOffTitle,
                HeroState.Connecting => Strings.MobileStatusConnectingTitle,
                HeroState.Unreachable => Strings.MobileStatusUnreachableTitle,
                HeroState.NeedsAttention => Strings.MobileStatusAttentionTitle,
                HeroState.HostSession => string.Format(UiFlow.Culture, Strings.MobileServiceSessionTitleFormat, sara),
                HeroState.GuestSession => string.Format(UiFlow.Culture, Strings.DesktopGuestSessionTitleFormat, sara),
                HeroState.SessionEnded => Strings.SessionPhaseEnded,
                _ => Strings.MobileStatusReadyTitle,
            },
            HeroText = hero switch
            {
                HeroState.Off => Strings.DesktopStatusOffText,
                HeroState.Connecting => Strings.MobileStatusConnectingText,
                HeroState.Unreachable => Strings.DesktopUnreachableText,
                HeroState.NeedsAttention => Strings.HostFirewallWarningMessage,
                HeroState.SessionEnded => string.Format(UiFlow.Culture, Strings.SessionSummaryFormat, Strings.SessionEndedByPeer,
                    string.Format(UiFlow.Culture, Strings.SessionDataUsedFormat, UiFlow.Ltr("18 MB"), UiFlow.Ltr("102 MB"))),
                _ => Strings.MobileReadyText,
            },
            HeroMotif = hero switch
            {
                HeroState.Off => MotifState.Off,
                HeroState.Connecting => MotifState.Connecting,
                HeroState.Unreachable => MotifState.Unreachable,
                HeroState.NeedsAttention => MotifState.Attention,
                HeroState.SessionEnded => MotifState.Ended,
                _ => MotifState.Ready,
            },
            Host = new HostFake
            {
                IsAvailable = hero != HeroState.Off,
                Notices =
                {
                    new HostNotice(HostNoticeKind.Vpn, Strings.HostVpnWarningTitle, string.Format(UiFlow.Culture, Strings.HostVpnWarningMessageFormat, UiFlow.Ltr("WireGuard"))),
                    new HostNotice(HostNoticeKind.SimulatedServer, Strings.HostSimulatedServerTitle, Strings.HostSimulatedServerHint),
                },
            },
            Session = new SessionFake
            {
                PeerText = string.Format(UiFlow.Culture, Strings.SessionPeerFormat, sara, UiFlow.Ltr("SARA-LAPTOP")),
                DataUsedText = string.Format(UiFlow.Culture, Strings.SessionDataUsedFormat, UiFlow.Ltr("18 MB"), UiFlow.Ltr("102 MB")),
                IsGuestActive = hero == HeroState.GuestSession,
                CanOpenCheckPage = hero == HeroState.GuestSession,
            },
            Contacts = Contacts(),
        };

        Window MainWith(MainFake fake, int tab = 0)
        {
            var window = new MainWindow(null!, null!) { DataContext = fake };
            window.Opened += (_, _) =>
            {
                foreach (var tabs in window.GetVisualDescendants().OfType<TabControl>())
                {
                    tabs.SelectedIndex = tab;
                }
            };
            return window;
        }

        yield return new("main-ready", () => MainWith(Main()));
        yield return new("main-off-device", () => MainWith(Main(HeroState.Off), tab: 1));
        yield return new("main-connecting", () => MainWith(Main(HeroState.Connecting)));
        yield return new("main-unreachable", () => MainWith(Main(HeroState.Unreachable), tab: 1));
        yield return new("main-attention", () => MainWith(Main(HeroState.NeedsAttention), tab: 1));
        yield return new("main-session-host", () =>
        {
            var m = Main(HeroState.HostSession);
            m.AutoNoteText = string.Format(UiFlow.Culture, Strings.MobileSessionAutoNoteFormat, sara);
            return MainWith(m);
        });
        yield return new("main-session-guest", () => MainWith(Main(HeroState.GuestSession)));
        yield return new("main-ended", () => MainWith(Main(HeroState.SessionEnded)));
        yield return new("main-loading", () =>
        {
            var m = Main(HeroState.Connecting);
            m.Contacts = new ContactsFake { IsLoading = true };
            return MainWith(m);
        });
        yield return new("main-empty", () =>
        {
            var m = Main();
            m.Contacts = new ContactsFake();
            return MainWith(m);
        });
        yield return new("main-error", () =>
        {
            var m = Main();
            m.Contacts.StatusMessage = string.Format(UiFlow.Culture, Strings.ContactsInviteFailedFormat, ar ? "لا يوجد حساب جسور بهذا البريد." : "That email has no Josour account.");
            m.Contacts.StatusIsError = true;
            m.Contacts.IsWaiting = true;
            m.Contacts.WaitingText = string.Format(UiFlow.Culture, Strings.GuestWaitingTextFormat, sara);
            return MainWith(m);
        });
        yield return new("main-remove", () =>
        {
            var m = Main();
            m.Contacts.Contacts[1].IsConfirmingRemove = true;
            return MainWith(m);
        });
        yield return new("main-trust", () =>
        {
            var m = Main();
            m.Contacts.IsConfirmingTrust = true;
            m.Contacts.TrustConfirmTitle = string.Format(UiFlow.Culture, Strings.MobileTrustConfirmTitleFormat, omar);
            return MainWith(m);
        });
        yield return new("main-quit", () =>
        {
            var m = Main(HeroState.HostSession);
            m.IsConfirmingQuit = true;
            m.ConfirmText = string.Format(UiFlow.Culture, Strings.DesktopConfirmEndsSessionFormat, sara);
            return MainWith(m);
        });
        yield return new("main-signout", () =>
        {
            var m = Main();
            m.IsConfirmingSignOut = true;
            m.ConfirmText = Strings.DesktopSignOutConfirmText;
            return MainWith(m, tab: 1);
        });

        IncomingRequest Request(AllowlistDisclosure scope) => new(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), sara, "SARA-LAPTOP", 30, DateTimeOffset.UtcNow.AddSeconds(60), scope);

        Window RequestWindow(AllowlistDisclosure scope, Action<IncomingRequestViewModel>? arrange = null)
        {
            var vm = new IncomingRequestViewModel(Request(scope), NullLogger<IncomingRequestViewModel>.Instance);
            arrange?.Invoke(vm);
            return new IncomingRequestWindow(vm);
        }

        yield return new("request", () => RequestWindow(AllowlistDisclosure.NoRestriction(3)));
        yield return new("request-sites", () => RequestWindow(new AllowlistDisclosure(4, new[] { "portal.example.org", "mail.example.org", "*.bank.example", "intranet.example.org:8443" }, true)));
        yield return new("request-trust", () => RequestWindow(AllowlistDisclosure.NoRestriction(3), vm => vm.TrustThisGuest = true));
        yield return new("request-unavailable", () => RequestWindow(AllowlistDisclosure.Unavailable(3)));
        yield return new("request-expired", () =>
        {
            var held = IncomingRequestWindow.ExpiredHold;
            IncomingRequestWindow.ExpiredHold = TimeSpan.FromHours(1);
            var window = RequestWindow(AllowlistDisclosure.NoRestriction(3), vm => vm.Expire());
            window.Closed += (_, _) => IncomingRequestWindow.ExpiredHold = held;
            return window;
        });

        yield return new("login", () => new LoginWindow(null!) { DataContext = new LoginFake() });
        yield return new("login-notice", () => new LoginWindow(null!) { DataContext = new LoginFake { NoticeText = Strings.SignedOutDeviceRevoked } });
        yield return new("login-error", () => new LoginWindow(null!) { DataContext = new LoginFake { ErrorText = Strings.LoginErrorInvalidCredentials } });
        yield return new("login-busy", () => new LoginWindow(null!) { DataContext = new LoginFake { IsIdle = false, SignInButtonText = Strings.LoginSigningIn } });
        yield return new("login-register", () => new LoginWindow(null!) { DataContext = new LoginFake { IsRegistering = true, DisplayName = sara } });

        Window FirstRun(FirstRunFake fake) => new FirstRunWindow(Uninitialized<FirstRunViewModel>()) { DataContext = fake };
        yield return new("firstrun-server", () => FirstRun(new FirstRunFake()));
        yield return new("firstrun-error", () => FirstRun(new FirstRunFake { Message = Strings.SettingsServerUnreachable, IsMessageAnError = true }));
        yield return new("firstrun-signin", () => FirstRun(new FirstRunFake { Step = 1 }));
        yield return new("firstrun-ready", () => FirstRun(new FirstRunFake { Step = 2 }));

        Window Settings(SettingsFake fake) => new SettingsWindow(Uninitialized<SettingsViewModel>()) { DataContext = fake };
        yield return new("settings", () => Settings(new SettingsFake()));
        yield return new("settings-ok", () => Settings(new SettingsFake { ServerMessage = Strings.SettingsServerOk, IsLanguageRestartNoteVisible = true }));
        yield return new("settings-error", () => Settings(new SettingsFake { ServerMessage = Strings.SettingsServerUnreachable, IsServerMessageAnError = true }));

        yield return new("about", () => new AboutWindow(Uninitialized<AboutViewModel>()) { DataContext = new AboutFake() });
        yield return new("about-copied", () => new AboutWindow(Uninitialized<AboutViewModel>()) { DataContext = new AboutFake { Message = Strings.AboutCopied } });

        AdminFake Admin() => new()
        {
            Users =
            {
                new UserFake { Initial = sara[..1], DisplayName = sara, Email = UiFlow.Ltr("sara@example.com"), IsAdmin = true, CanToggleActive = false },
                new UserFake { Initial = omar[..1], DisplayName = omar, Email = UiFlow.Ltr("omar@example.com") },
                new UserFake { Initial = huda[..1], DisplayName = huda, Email = UiFlow.Ltr("huda@example.com"), StateText = Strings.AdminStateLocked, IsStateActive = false, IsStateLocked = true, CanUnlock = true },
                new UserFake { Initial = ahmad[..1], DisplayName = ahmad, Email = UiFlow.Ltr("ahmad@example.com"), StateText = Strings.AdminStateDisabled, IsStateActive = false, IsStateDisabled = true, ToggleActiveText = Strings.AdminEnable },
            },
        };
        Window AdminWindowWith(AdminFake fake) => new AdminWindow(Uninitialized<AdminViewModel>()) { DataContext = fake };
        yield return new("admin", () => AdminWindowWith(Admin()));
        yield return new("admin-loading", () => AdminWindowWith(new AdminFake { IsLoading = true }));
        yield return new("admin-empty", () => AdminWindowWith(new AdminFake()));
        yield return new("admin-disable", () =>
        {
            var a = Admin();
            a.IsConfirmingDisable = true;
            a.DisableConfirmTitle = string.Format(UiFlow.Culture, Strings.DesktopAdminDisableTitleFormat, omar);
            return AdminWindowWith(a);
        });

        yield return new("licenses", () => new LicensesWindow());
    }

    /// <summary>A view model with no services, only to satisfy a window's constructor; the window then shows a fake.</summary>
    private static T Uninitialized<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
}
