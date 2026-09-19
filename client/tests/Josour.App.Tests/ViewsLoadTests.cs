using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using Josour.App.Controls;
using Josour.App.Models;
using Josour.App.ViewModels;
using Josour.App.Views;
using Josour.Infrastructure.Session;
using Josour.Proxy;

namespace Josour.App.Tests;

/// <summary>
/// The views, loaded for real.
/// <para>
/// The WPF build had no test project at all — the request window was the one screen in the app with nothing checking
/// it, which is also the screen the host's consent is taken on. Moving to Avalonia is what made these possible: the
/// headless platform loads the same XAML, applies the same themes and runs the same bindings with no display.
/// </para>
/// </summary>
public class ViewsLoadTests
{
    private static IncomingRequest SampleRequest(int durationMinutes = 30) => new(
        RequestId: Guid.NewGuid(),
        GuestUserId: Guid.NewGuid(),
        GuestDeviceId: Guid.NewGuid(),
        GuestName: "سارة أحمد",
        GuestDevice: "SARA-LAPTOP",
        DurationMinutes: durationMinutes,
        ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(60),
        Allowlist: AllowlistDisclosure.NoRestriction(3));

    private static IncomingRequestViewModel SampleViewModel(int durationMinutes = 30) =>
        new(SampleRequest(durationMinutes), NullLogger<IncomingRequestViewModel>.Instance);

    /// <summary>Renders a control off-screen so that templates are applied and bindings evaluated.</summary>
    private static Rendered Render(Control content) => new(content);

    [AvaloniaTheory]
    [InlineData(typeof(HostPage))]
    [InlineData(typeof(GuestPage))]
    [InlineData(typeof(SessionPanel))]
    public void A_page_loads_its_xaml_and_applies_its_templates(Type pageType)
    {
        var page = (Control)Activator.CreateInstance(pageType)!;

        using var rendered = Render(page);

        Assert.NotNull(page.GetVisualRoot());
        Assert.NotEmpty(page.GetVisualDescendants());
    }

    [AvaloniaFact]
    public void The_request_window_shows_who_is_asking_and_for_how_long()
    {
        using var viewModel = SampleViewModel(durationMinutes: 45);
        using var rendered = new Rendered(new IncomingRequestWindow(viewModel));
        var window = rendered.Window;

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToList();

        Assert.Contains(texts, t => t.Contains("سارة أحمد", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains("SARA-LAPTOP", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains("45", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void The_trust_checkbox_starts_unticked()
    {
        // docs/ws-protocol.md section 5a. A standing consent must never be something the host gives by not noticing
        // a pre-ticked box, so this is worth asserting rather than assuming.
        using var viewModel = SampleViewModel();
        using var rendered = new Rendered(new IncomingRequestWindow(viewModel));
        var window = rendered.Window;

        var checkBox = window.GetVisualDescendants().OfType<CheckBox>().Single();

        Assert.False(checkBox.IsChecked);
        Assert.False(viewModel.TrustThisGuest);
        Assert.Null(viewModel.Trust);
    }

    [AvaloniaFact]
    public void Ticking_the_box_offers_a_rule_capped_at_this_request_and_expiring_by_default()
    {
        using var viewModel = SampleViewModel(durationMinutes: 45);
        using var rendered = new Rendered(new IncomingRequestWindow(viewModel));
        var window = rendered.Window;

        window.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked = true;

        var trust = viewModel.Trust;
        Assert.NotNull(trust);
        Assert.Equal(45, trust!.MaxDurationMinutes);
        Assert.Equal(TimeSpan.FromDays(7), trust.For); // a week, not "always"
    }

    [AvaloniaFact]
    public void The_duration_box_is_disabled_until_the_box_is_ticked()
    {
        using var viewModel = SampleViewModel();
        using var rendered = new Rendered(new IncomingRequestWindow(viewModel));
        var window = rendered.Window;

        var combo = window.GetVisualDescendants().OfType<ComboBox>().Single();
        Assert.False(combo.IsEffectivelyEnabled);

        window.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked = true;
        Assert.True(combo.IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void An_arabic_window_reads_right_to_left()
    {
        // The interface language is fixed before any window exists, and Arabic is the shipped default.
        using var viewModel = SampleViewModel();
        using var rendered = new Rendered(new IncomingRequestWindow(viewModel));
        var window = rendered.Window;

        Assert.Equal(UiFlow.Direction, window.FlowDirection);
        Assert.Equal(FlowDirection.RightToLeft, window.FlowDirection);
    }

    [AvaloniaFact]
    public void The_guest_page_offers_the_role_only_where_the_platform_can_police_it()
    {
        // The proxy listens on loopback; without an owner check every program on the machine could leave
        // through the host's address. So the page either offers the role or says why it cannot — never both,
        // and never neither.
        var page = new GuestPage();

        using var rendered = Render(page);

        var bars = rendered.Window.GetVisualDescendants().OfType<InfoBar>().ToList();
        var warning = bars.SingleOrDefault(b => b.Title == Strings.GuestRoleUnavailableTitle);

        Assert.NotNull(warning);
        Assert.Equal(!OwnerPidCheckers.SupportedOnThisPlatform, warning!.IsVisible);
        Assert.Equal(OwnerPidCheckers.SupportedOnThisPlatform, GuestViewModel.IsGuestRoleSupported);
    }

    [AvaloniaFact]
    public void The_fatal_error_window_opens_without_the_app_being_healthy()
    {
        // Its whole job is to appear when something in the composition root has already thrown.
        FatalErrorWindow.Show("عنوان", "رسالة");
    }
}
