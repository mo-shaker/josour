using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Josour.Infrastructure.Api;

namespace Josour.App.ViewModels;

/// <summary>
/// The admin panel: list accounts, create one, enable or disable one, unlock one, set a password.
/// <para>
/// It replaces reaching for <c>manage.py</c> over SSH, which was the only way to add a user. What it deliberately
/// does NOT offer is deletion — and that is not an omission. <c>sessions.guest_user_id</c> and <c>host_user_id</c>
/// are both <c>ON DELETE CASCADE</c>, so deleting an account erases the record of every session it was part of,
/// including the host's half. Disabling takes the person off the system just as completely and leaves the history
/// of the people they shared it with intact.
/// </para>
/// <para>
/// The server enforces all of this; the panel only appears for an administrator as a courtesy. Every call here can
/// answer 403 and is written to survive it.
/// </para>
/// </summary>
public sealed partial class AdminViewModel : ObservableObject
{
    private readonly IApiClient _api;
    private readonly IAuthSession _auth;
    private readonly TimeProvider _time;
    private readonly ILogger<AdminViewModel> _logger;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateUserCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string _message = string.Empty;

    [ObservableProperty]
    private bool _isMessageAnError;

    // ---- the create form ----

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateUserCommand))]
    private string _newEmail = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateUserCommand))]
    private string _newDisplayName = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateUserCommand))]
    private string _newPassword = string.Empty;

    [ObservableProperty]
    private bool _newIsAdmin;

    public AdminViewModel(
        IApiClient api,
        IAuthSession auth,
        ILogger<AdminViewModel> logger,
        TimeProvider? time = null)
    {
        _api = api;
        _auth = auth;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The accounts, as listed.</summary>
    public ObservableCollection<AdminUserRow> Users { get; } = new();

    public bool IsIdle => !IsBusy;

    public bool HasMessage => Message.Length > 0;

    public bool HasUsers => Users.Count > 0;

    /// <summary>The shortest password the server will take; stated so the form can say it before the server refuses.</summary>
    public const int MinimumPasswordLength = 8;

    private bool CanAct() => !IsBusy;

    private bool CanCreateUser() =>
        !IsBusy
        && NewEmail.Trim().Length > 0
        && NewDisplayName.Trim().Length > 0
        && NewPassword.Length >= MinimumPasswordLength;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task RefreshAsync(CancellationToken ct)
    {
        await RunAsync(
            async () =>
            {
                var users = await _api.GetUsersAsync(SearchText, ct).ConfigureAwait(true);
                var me = _auth.CurrentUser?.Id;
                var now = _time.GetUtcNow();

                Users.Clear();
                foreach (var user in users)
                {
                    Users.Add(AdminUserRow.From(user, me, now));
                }

                OnPropertyChanged(nameof(HasUsers));
            },
            successMessage: null);
    }

    [RelayCommand(CanExecute = nameof(CanCreateUser))]
    private async Task CreateUserAsync(CancellationToken ct)
    {
        var email = NewEmail.Trim();
        await RunAsync(
            async () =>
            {
                await _api.CreateUserAsync(
                    new AdminUserCreate(email, NewPassword, NewDisplayName.Trim(), NewIsAdmin ? "admin" : "user"),
                    ct).ConfigureAwait(true);

                // The password never stays in a field after it has been used.
                NewEmail = string.Empty;
                NewDisplayName = string.Empty;
                NewPassword = string.Empty;
                NewIsAdmin = false;
                await RefreshAsync(ct).ConfigureAwait(true);
            },
            successMessage: string.Format(UiFlow.Culture, Strings.AdminUserCreatedFormat, UiFlow.Ltr(email)));
    }

    /// <summary>
    /// Enable or disable an account. Disabling is immediate on the server: tokens revoked, presence cleared, live
    /// control channels closed. The message says so, because "disabled" that only takes effect later is the kind of
    /// half-truth an administrator acts on.
    /// </summary>
    [RelayCommand]
    private async Task ToggleActiveAsync(AdminUserRow? row)
    {
        if (row is null || !row.CanToggleActive)
        {
            return;
        }

        var enabling = !row.IsActive;
        await RunAsync(
            async () =>
            {
                await _api.PatchUserAsync(row.Id, new AdminUserPatch { IsActive = enabling }, CancellationToken.None)
                    .ConfigureAwait(true);
                await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
            },
            successMessage: string.Format(
                UiFlow.Culture,
                enabling ? Strings.AdminUserEnabledFormat : Strings.AdminUserDisabledFormat,
                row.DisplayName));
    }

    [RelayCommand]
    private async Task UnlockAsync(AdminUserRow? row)
    {
        if (row is null || !row.CanUnlock)
        {
            return;
        }

        await RunAsync(
            async () =>
            {
                await _api.PatchUserAsync(row.Id, new AdminUserPatch { Unlock = true }, CancellationToken.None)
                    .ConfigureAwait(true);
                await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
            },
            successMessage: string.Format(UiFlow.Culture, Strings.AdminUserUnlockedFormat, row.DisplayName));
    }

    /// <summary>Sets a new password. The server revokes the account's refresh tokens, so its devices sign in again.</summary>
    public async Task SetPasswordAsync(AdminUserRow row, string password)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (password is null || password.Length < MinimumPasswordLength)
        {
            Message = string.Format(UiFlow.Culture, Strings.AdminPasswordTooShortFormat, MinimumPasswordLength);
            IsMessageAnError = true;
            return;
        }

        await RunAsync(
            () => _api.PatchUserAsync(row.Id, new AdminUserPatch { Password = password }, CancellationToken.None),
            successMessage: string.Format(UiFlow.Culture, Strings.AdminPasswordSetFormat, row.DisplayName));
    }

    /// <summary>
    /// One place where every call reports the same way: busy while it runs, one sentence afterwards, and a failure
    /// that never throws out of a command. The API layer already turns a status code into a message.
    /// </summary>
    private async Task RunAsync(Func<Task> action, string? successMessage)
    {
        IsBusy = true;
        Message = string.Empty;
        try
        {
            await action().ConfigureAwait(true);
            if (successMessage is not null)
            {
                Message = successMessage;
                IsMessageAnError = false;
            }
        }
        catch (OperationCanceledException)
        {
            // The window closed while the call was in flight.
        }
        catch (ApiException ex)
        {
            _logger.LogWarning(ex, "An admin call failed: {Status}", ex.StatusCode);
            Message = ex.Message;
            IsMessageAnError = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An admin call failed unexpectedly");
            Message = ex.Message;
            IsMessageAnError = true;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
