using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NdeipiChat.Client.Auth;
using NdeipiChat.Client.Livestock;
using NdeipiChat.Client.Realtime;
using NdeipiChat.Contracts;

namespace NdeipiChat.Client.ViewModels;

/// <summary>Launch and sign-out: decides whether the app opens on sign-in or on the chats.</summary>
public sealed class AppCoordinator(AuthService auth, ChatSession session, LivestockSync livestock, INavigator navigator, IUiDispatcher ui)
{
    bool _started;

    public async Task StartAsync()
    {
        if (!_started)
        {
            _started = true;
            auth.SignedOut += () => ui.Post(async () =>
            {
                await session.StopAsync();
                await navigator.ShowSignInAsync();
            });
        }

        if (await auth.IsSignedInAsync())
            await SignedInAsync();
        else
            await navigator.ShowSignInAsync();
    }

    public async Task SignedInAsync()
    {
        try
        {
            await session.StartAsync();
        }
        catch (ApiException ex) when (ex.StatusCode is null)
        {
            // Offline: open the app anyway; the connection keeps retrying in the background.
        }
        _ = SendWaitingCapturesAsync();
        await navigator.ShowMainAsync();
    }

    /// <summary>Livestock captures taken offline go up as soon as the app is back.</summary>
    public async Task SendWaitingCapturesAsync()
    {
        try
        {
            await livestock.UploadPendingAsync();
        }
        catch (Exception ex) when (ex is IOException or ApiException or InvalidOperationException)
        {
            // Stays queued for the next launch or connectivity change.
        }
    }
}

public sealed partial class SignInViewModel(AuthService auth, AppCoordinator app) : ObservableObject
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    async Task SignInAsync()
    {
        ErrorMessage = null;
        IsBusy = true;
        try
        {
            await auth.SignInAsync();
            await app.SignedInAsync();
        }
        catch (OperationCanceledException)
        {
            // The browser was closed; nothing to report.
        }
        catch (Exception ex) when (ex is AuthException or ApiException)
        {
            ErrorMessage = ex.Message;
        }
        catch (HttpRequestException)
        {
            ErrorMessage = "Can't reach the server. Check your connection.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    bool CanSignIn() => !IsBusy;
}

public sealed partial class MeViewModel : ObservableObject
{
    readonly ChatSession _session;
    readonly AuthService _auth;
    readonly INavigator _navigator;
    readonly IDialogs _dialogs;

    public MeViewModel(ChatSession session, AuthService auth, INavigator navigator, IDialogs dialogs, IUiDispatcher ui)
    {
        (_session, _auth, _navigator, _dialogs) = (session, auth, navigator, dialogs);
        _session.MeChanged += me => ui.Post(() => Me = me);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName), nameof(Initials), nameof(Email), nameof(AvatarUrl), nameof(Username))]
    public partial MeDto? Me { get; set; }

    public string DisplayName => Me?.DisplayName ?? "";
    public string Initials => Display.Initials(Me?.DisplayName);
    public string? Email => Me?.Email;
    public string? AvatarUrl => Me?.AvatarUrl;
    public string? Username => Me?.Username is { } u ? "@" + u : null;

    [RelayCommand]
    async Task LoadAsync()
    {
        try
        {
            Me = await _session.RefreshMeAsync();
        }
        catch (ApiException ex)
        {
            Me ??= _session.Me;
            if (Me is null)
                await _dialogs.AlertAsync("Couldn't load your profile", ex.Message);
        }
    }

    [RelayCommand]
    Task OpenWalletAsync() => _navigator.GoToAsync(Routes.Wallet);

    /// <summary>AuthService raises SignedOut; <see cref="AppCoordinator"/> takes it from there.</summary>
    [RelayCommand]
    Task SignOutAsync() => _auth.SignOutAsync();
}

/// <summary>
/// Wallet: Bridge identity verification and balance, plus the addresses the user receives tokens at.
/// </summary>
public sealed partial class WalletViewModel : ObservableObject
{
    readonly ChatApi _api;
    readonly ChatSession _session;
    readonly IDialogs _dialogs;

    public WalletViewModel(ChatApi api, ChatSession session, IDialogs dialogs, IUiDispatcher ui)
    {
        (_api, _session, _dialogs) = (api, session, dialogs);
        NewWalletChain = "";
        NewWalletAddress = "";
        _session.Connection.BankingStatusChanged += status => ui.Post(async () =>
        {
            Status = status;
            await LoadBalancesAsync();
        });
    }

    public ObservableCollection<BalanceDto> Balances { get; } = [];
    public ObservableCollection<UserWalletDto> ReceivingWallets { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(VerifyButtonText), nameof(CanVerify), nameof(IsVerified), nameof(WalletText))]
    public partial BankingStatusDto? Status { get; set; }

    [ObservableProperty]
    public partial string NewWalletChain { get; set; }

    [ObservableProperty]
    public partial string NewWalletAddress { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public bool IsVerified => Status?.CanTransfer == true;

    public string WalletText => Status?.WalletAddress is { } address ? $"{Status.WalletChain} · {Display.ShortAddress(address)}" : "";

    public string StatusText => Status switch
    {
        null => "",
        { CanTransfer: true } => "Verified",
        { KycStatus: "none" or "not_started" } => "Not verified",
        { KycStatus: "incomplete" } => "Verification not finished",
        { KycStatus: "under_review" or "awaiting_ubo" } => "Under review",
        { KycStatus: "rejected" } => "Verification unsuccessful",
        { KycStatus: "paused" or "offboarded" } => "Account on hold",
        { KycStatus: "approved", TosStatus: not "approved" } => "Accept the terms to finish",
        { KycStatus: "approved" } => "Setting up your wallet…",
        _ => Status.KycStatus
    };

    public string VerifyButtonText => Status switch
    {
        null or { CanTransfer: true } => "",
        { KycStatus: "none" or "not_started" } => "Verify identity",
        { KycStatus: "incomplete" } or { TosStatus: not "approved" } => "Continue verification",
        _ => ""
    };

    public bool CanVerify => VerifyButtonText.Length > 0;

    [RelayCommand]
    async Task LoadAsync()
    {
        await RunAsync(async () =>
        {
            Status = await _api.GetBankingStatusAsync();
            var me = await _session.RefreshMeAsync();
            ReceivingWallets.Clear();
            foreach (var wallet in me.Wallets)
                ReceivingWallets.Add(wallet);
            await LoadBalancesAsync();
        });
    }

    /// <summary>Bridge wants its terms accepted before KYC, so open whichever step is next.</summary>
    [RelayCommand]
    async Task VerifyAsync()
    {
        await RunAsync(async () =>
        {
            var link = await _api.StartKycAsync();
            Status = link.Status;
            var next = link.Status.TosStatus != "approved" && link.TosUrl.Length > 0 ? link.TosUrl : link.KycUrl;
            if (next.Length > 0)
                await _dialogs.OpenBrowserAsync(new Uri(next));
        });
    }

    [RelayCommand]
    async Task RefreshAsync()
    {
        await RunAsync(async () =>
        {
            Status = await _api.RefreshBankingAsync();
            await LoadBalancesAsync();
        });
    }

    [RelayCommand]
    async Task SaveWalletAsync()
    {
        if (string.IsNullOrWhiteSpace(NewWalletChain) || string.IsNullOrWhiteSpace(NewWalletAddress))
        {
            ErrorMessage = "Enter a chain and an address.";
            return;
        }
        await RunAsync(async () =>
        {
            var saved = await _api.SaveWalletAsync(new UserWalletDto(NewWalletChain.Trim(), NewWalletAddress.Trim()));
            var existing = ReceivingWallets.FirstOrDefault(w => w.Chain == saved.Chain);
            if (existing is not null)
                ReceivingWallets.Remove(existing);
            ReceivingWallets.Add(saved);
            NewWalletChain = "";
            NewWalletAddress = "";
        });
    }

    [RelayCommand]
    async Task RemoveWalletAsync(UserWalletDto wallet)
    {
        await RunAsync(async () =>
        {
            await _api.RemoveWalletAsync(wallet.Chain);
            ReceivingWallets.Remove(wallet);
        });
    }

    async Task LoadBalancesAsync()
    {
        Balances.Clear();
        if (!IsVerified)
            return;
        try
        {
            foreach (var balance in await _api.GetBalancesAsync())
                Balances.Add(balance);
        }
        catch (ApiException ex) when (ex.StatusCode is HttpStatusCode.BadGateway)
        {
            ErrorMessage = "Your balance isn't available right now.";
        }
    }

    async Task RunAsync(Func<Task> work)
    {
        ErrorMessage = null;
        IsBusy = true;
        try
        {
            await work();
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
