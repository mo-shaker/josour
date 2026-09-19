using System.Net;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using Josour.App.ViewModels;
using Josour.App.Views;
using Josour.Infrastructure.Api;

namespace Josour.App.Tests;

/// <summary>
/// The admin panel, which replaced reaching for <c>manage.py</c> over SSH. What is worth asserting here is not that
/// the list renders, but the three refusals: no delete, no disabling yourself, and no pretending a 403 succeeded.
/// </summary>
public class AdminPanelTests
{
    private static readonly Guid AdminId = new("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid OtherId = new("bbbbbbbb-0000-4000-8000-000000000002");
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    private static AdminUserDto User(
        Guid id, string email = "x@example.com", string role = "user",
        bool isActive = true, DateTimeOffset? lockedUntil = null, int failedLogins = 0) =>
        new(id, email, "Sara", role, isActive, failedLogins, lockedUntil, Now.AddDays(-30));

    // ---------------------------------------------------------------- the row's own rules

    [Fact]
    public void An_administrator_cannot_disable_their_own_account()
    {
        // The only action in the panel that can lock the last administrator out of their own server.
        var self = AdminUserRow.From(User(AdminId, role: "admin"), currentUserId: AdminId, Now);
        var other = AdminUserRow.From(User(OtherId), currentUserId: AdminId, Now);

        Assert.True(self.IsSelf);
        Assert.False(self.CanToggleActive);
        Assert.True(other.CanToggleActive);
    }

    [Fact]
    public void Disabled_and_locked_out_are_different_states_with_different_words()
    {
        // One is a decision somebody took; the other is what repeated failures did on their own and clears itself.
        // A single "blocked" badge covering both makes an administrator unlock an account nobody locked.
        var disabled = AdminUserRow.From(User(OtherId, isActive: false), AdminId, Now);
        var locked = AdminUserRow.From(User(OtherId, lockedUntil: Now.AddMinutes(10)), AdminId, Now);
        var active = AdminUserRow.From(User(OtherId), AdminId, Now);

        Assert.Equal(Strings.AdminStateDisabled, disabled.StateText);
        Assert.Equal(Strings.AdminStateLocked, locked.StateText);
        Assert.Equal(Strings.AdminStateActive, active.StateText);
        Assert.True(locked.CanUnlock);
        Assert.False(active.CanUnlock);
        Assert.False(disabled.CanUnlock);
    }

    [Fact]
    public void A_lockout_that_has_already_run_out_is_not_a_lockout()
    {
        var expired = AdminUserRow.From(User(OtherId, lockedUntil: Now.AddSeconds(-1)), AdminId, Now);

        Assert.False(expired.IsLocked);
        Assert.Equal(Strings.AdminStateActive, expired.StateText);
    }

    // ---------------------------------------------------------------- the view model

    private sealed class FakeApi : StubApiClient
    {
        public List<AdminUserDto> Accounts { get; } = new();
        public List<(Guid Id, AdminUserPatch Patch)> Patches { get; } = new();
        public ApiException? Failure { get; set; }

        public override Task<IReadOnlyList<AdminUserDto>> GetUsersAsync(string? query, CancellationToken ct)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            return Task.FromResult<IReadOnlyList<AdminUserDto>>(Accounts);
        }

        public override Task<AdminUserDto> PatchUserAsync(Guid userId, AdminUserPatch patch, CancellationToken ct)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            Patches.Add((userId, patch));
            return Task.FromResult(Accounts[0]);
        }
    }

    private static AdminViewModel Build(FakeApi api, string role = "admin") =>
        new(api, new StubAuthSession(new UserDto(AdminId, "admin@example.com", "Admin", role)),
            NullLogger<AdminViewModel>.Instance, new FixedTime(Now));

    [Fact]
    public async Task Disabling_sends_is_active_false_and_nothing_else()
    {
        // The server rejects unknown keys rather than ignoring them, so the patch must carry only what changed.
        var api = new FakeApi();
        api.Accounts.Add(User(OtherId));
        var vm = Build(api);
        await vm.RefreshCommand.ExecuteAsync(null);

        await vm.ToggleActiveCommand.ExecuteAsync(vm.Users.Single());

        var (id, patch) = api.Patches[0];
        Assert.Equal(OtherId, id);
        Assert.False(patch.IsActive);
        Assert.Null(patch.Password);
        Assert.Null(patch.DisplayName);
        Assert.Null(patch.Unlock);
    }

    [Fact]
    public async Task A_refused_call_is_reported_and_never_thrown_out_of_a_command()
    {
        // Every admin endpoint answers 403 to a non-administrator whatever the app shows. The panel has to
        // survive being wrong about the role, not assume it never will be.
        var api = new FakeApi { Failure = new ApiException(HttpStatusCode.Forbidden, "forbidden", "Admin role required") };
        var vm = Build(api);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.True(vm.HasMessage);
        Assert.True(vm.IsMessageAnError);
        Assert.Contains("Admin", vm.Message, StringComparison.Ordinal);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task A_password_shorter_than_the_server_accepts_is_refused_here_first()
    {
        var api = new FakeApi();
        api.Accounts.Add(User(OtherId));
        var vm = Build(api);
        await vm.RefreshCommand.ExecuteAsync(null);

        await vm.SetPasswordAsync(vm.Users.Single(), "short");

        Assert.True(vm.IsMessageAnError);
        Assert.Empty(api.Patches);
    }

    [Fact]
    public async Task The_create_form_will_not_submit_until_it_can_succeed()
    {
        var vm = Build(new FakeApi());

        Assert.False(vm.CreateUserCommand.CanExecute(null));

        vm.NewEmail = "new@example.com";
        vm.NewDisplayName = "New";
        vm.NewPassword = "short";
        Assert.False(vm.CreateUserCommand.CanExecute(null));

        vm.NewPassword = new string('x', AdminViewModel.MinimumPasswordLength);
        Assert.True(vm.CreateUserCommand.CanExecute(null));
    }

    // ---------------------------------------------------------------- the window

    [AvaloniaFact]
    public void The_window_loads_and_warns_that_disabling_is_immediate()
    {
        // "Disabled" that only took effect later is the kind of half-truth an administrator acts on.
        var vm = Build(new FakeApi());

        using var rendered = new Rendered(new AdminWindow(vm));

        var messages = rendered.Window.GetVisualDescendants().OfType<Controls.InfoBar>()
            .Select(b => b.Message ?? string.Empty).ToList();
        Assert.Contains(messages, m => m == Strings.AdminDisableWarning);
    }

    [AvaloniaFact]
    public void The_window_says_the_list_is_empty_rather_than_showing_nothing()
    {
        var vm = Build(new FakeApi());

        using var rendered = new Rendered(new AdminWindow(vm));

        var texts = rendered.Window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsVisible).Select(t => t.Text ?? string.Empty).ToList();
        Assert.Contains(Strings.AdminEmptyList, texts);
    }
}
