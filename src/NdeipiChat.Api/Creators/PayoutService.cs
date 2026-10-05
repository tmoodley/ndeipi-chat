using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NdeipiChat.Api.Banking;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Creators;

/// <summary>
/// Withdrawals (FR-CR-13): from the creator's Ndeipi wallet to a US bank account (ACH or wire), a euro
/// bank account (SEPA, by IBAN) or a USDC address, through Bridge. Bank details go straight to Bridge;
/// Ndeipi keeps Bridge's id and the last four digits. Earnings from the last
/// <see cref="CreatorsContract.HoldDays"/> days stay on hold.
/// </summary>
public sealed partial class PayoutService(
    ChatDbContext db,
    BridgeClient bridgeClient,
    CreatorBilling billing,
    IOptions<BridgeOptions> bridge,
    TimeProvider clock,
    ILogger<PayoutService> log)
{
    DateTimeOffset Now => clock.GetUtcNow();

    async Task<BankingProfile> ReadyAsync(User user, CancellationToken ct)
    {
        if (!await db.CreatorProfiles.AnyAsync(c => c.UserId == user.Id, ct))
            throw new ChatRejectedException("Set up your creator page first.");
        if (!bridge.Value.IsConfigured)
            throw new ChatRejectedException("Withdrawals aren't set up on this server.");
        return await db.BankingProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == user.Id, ct) is { CanTransfer: true } profile
            ? profile
            : throw new ChatRejectedException("Your Ndeipi wallet isn't ready: verify your identity under Me > Wallet.");
    }

    public async Task<PayoutsDto> PayoutsAsync(User user, CancellationToken ct)
    {
        await ReadyAsync(user, ct);
        var (_, onHold, withdrawable) = await billing.WithdrawableAsync(user.Id, ct);
        var accounts = await db.PayoutAccounts.AsNoTracking().Where(a => a.UserId == user.Id && !a.Removed).OrderBy(a => a.CreatedAt).ToListAsync(ct);
        var withdrawals = await db.Withdrawals.AsNoTracking().Where(w => w.UserId == user.Id).OrderByDescending(w => w.CreatedAt).Take(50).ToListAsync(ct);
        var labels = await db.PayoutAccounts.AsNoTracking().Where(a => a.UserId == user.Id).ToDictionaryAsync(a => a.Id, a => a.Label, ct);
        return new PayoutsDto(withdrawable, onHold,
            accounts.Select(ToDto).ToList(),
            withdrawals.Select(w => new WithdrawalDto(w.Id, labels.GetValueOrDefault(w.PayoutAccountId, ""), w.Amount, w.Status, w.Error, w.CreatedAt)).ToList());
    }

    static PayoutAccountDto ToDto(PayoutAccount a) => new(a.Id, a.Rail, a.Label, a.CreatedAt);

    [GeneratedRegex(@"^\d{4,17}$")] private static partial Regex AccountNumber();
    [GeneratedRegex(@"^\d{9}$")] private static partial Regex RoutingNumber();
    [GeneratedRegex(@"^[A-Z]{2}\d{2}[A-Z0-9]{10,30}$")] private static partial Regex Iban();
    [GeneratedRegex(@"^[1-9A-HJ-NP-Za-km-z]{32,44}$")] private static partial Regex SolanaAddress();
    [GeneratedRegex(@"^0x[0-9a-fA-F]{40}$")] private static partial Regex EvmAddress();

    public async Task<PayoutAccountDto> AddAccountAsync(User user, SavePayoutAccountRequest r, CancellationToken ct)
    {
        var profile = await ReadyAsync(user, ct);
        var owner = CreatorService.Required(r.OwnerName, 100, "Enter the account holder's name.");
        var account = new PayoutAccount { Id = Guid.NewGuid(), UserId = user.Id, Rail = r.Rail, Label = "", Currency = "usd", CreatedAt = Now };

        switch (r.Rail)
        {
            case PayoutRails.Crypto:
            {
                var address = r.Address?.Trim() ?? "";
                var chain = bridge.Value.WalletChain;
                var valid = chain == "solana" ? SolanaAddress().IsMatch(address) : EvmAddress().IsMatch(address);
                if (!valid)
                    throw new ChatRejectedException($"Enter a {chain} address for {bridge.Value.Currency.ToUpperInvariant()}.");
                (account.Address, account.Currency) = (address, bridge.Value.Currency);
                account.Label = $"{bridge.Value.Currency.ToUpperInvariant()} {address[..4]}…{address[^4..]}";
                break;
            }
            case PayoutRails.Ach or PayoutRails.Wire:
            {
                var number = Digits(r.AccountNumber);
                var routing = Digits(r.RoutingNumber);
                if (!AccountNumber().IsMatch(number) || !RoutingNumber().IsMatch(routing))
                    throw new ChatRejectedException("Enter the account number and the 9-digit routing number.");
                var bank = CreatorService.Required(r.BankName, 80, "Enter the bank's name.");
                var created = await CreateExternalAsync(profile, account.Id, new
                {
                    currency = "usd",
                    bank_name = bank,
                    account_owner_name = owner,
                    account_type = "us",
                    account = new { account_number = number, routing_number = routing, checking_or_savings = r.CheckingOrSavings == "savings" ? "savings" : "checking" },
                    address = new
                    {
                        street_line_1 = CreatorService.Required(r.StreetLine1, 100, "Enter the account holder's street address."),
                        city = CreatorService.Required(r.City, 60, "Enter the city."),
                        state = CreatorService.Required(r.State, 30, "Enter the state."),
                        postal_code = CreatorService.Required(r.PostalCode, 12, "Enter the ZIP code."),
                        country = "USA"
                    }
                }, ct);
                (account.BridgeExternalAccountId, account.Currency) = (created.Id, "usd");
                account.Label = $"{bank} ••{created.Last4 ?? number[^4..]}";
                break;
            }
            case PayoutRails.Sepa:
            {
                var iban = (r.Iban ?? "").Replace(" ", "").ToUpperInvariant();
                if (!Iban().IsMatch(iban))
                    throw new ChatRejectedException("Enter a valid IBAN.");
                var names = owner.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                var created = await CreateExternalAsync(profile, account.Id, new
                {
                    currency = "eur",
                    account_owner_name = owner,
                    account_type = "iban",
                    account_owner_type = "individual",
                    first_name = names[0],
                    last_name = names.Length > 1 ? names[1] : names[0],
                    iban = new { account_number = iban, bic = string.IsNullOrWhiteSpace(r.Bic) ? null : r.Bic.Trim().ToUpperInvariant(), country = Alpha3(iban[..2]) }
                }, ct);
                (account.BridgeExternalAccountId, account.Currency) = (created.Id, "eur");
                account.Label = $"IBAN ••{created.Last4 ?? iban[^4..]}";
                break;
            }
            default:
                throw new ChatRejectedException("Choose a US bank account, an IBAN or a USDC address.");
        }

        db.PayoutAccounts.Add(account);
        await db.SaveChangesAsync(ct);
        return ToDto(account);
    }

    async Task<BridgeExternalAccount> CreateExternalAsync(BankingProfile profile, Guid accountId, object body, CancellationToken ct)
    {
        try
        {
            return await bridgeClient.CreateExternalAccountAsync(profile.BridgeCustomerId!, body, $"external-account-{accountId}", ct);
        }
        catch (BridgeApiException ex) when (ex.IsClientError)
        {
            log.LogWarning("Bridge refused a payout account for {UserId}: {Status}", profile.UserId, (int)ex.StatusCode);
            throw new ChatRejectedException("The bank details weren't accepted. Check them and try again.");
        }
    }

    static string Digits(string? s) => new((s ?? "").Where(char.IsDigit).ToArray());

    /// <summary>Bridge wants ISO 3166 alpha-3 countries; IBANs start with alpha-2.</summary>
    static string Alpha3(string alpha2) => alpha2 switch
    {
        "DE" => "DEU", "FR" => "FRA", "NL" => "NLD", "BE" => "BEL", "ES" => "ESP", "IT" => "ITA", "PT" => "PRT", "IE" => "IRL",
        "AT" => "AUT", "FI" => "FIN", "LU" => "LUX", "GR" => "GRC", "SK" => "SVK", "SI" => "SVN", "EE" => "EST", "LV" => "LVA",
        "LT" => "LTU", "MT" => "MLT", "CY" => "CYP", "GB" => "GBR", "CH" => "CHE", "SE" => "SWE", "DK" => "DNK", "NO" => "NOR",
        "PL" => "POL", "CZ" => "CZE", "HU" => "HUN", "RO" => "ROU", "BG" => "BGR", "HR" => "HRV",
        _ => throw new ChatRejectedException("That IBAN's country isn't supported for withdrawals.")
    };

    public async Task<bool> RemoveAccountAsync(User user, Guid id, CancellationToken ct) =>
        await db.PayoutAccounts.Where(a => a.Id == id && a.UserId == user.Id && !a.Removed)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Removed, true), ct) > 0;

    public async Task<WithdrawalDto> WithdrawAsync(User user, WithdrawRequest r, CancellationToken ct)
    {
        var profile = await ReadyAsync(user, ct);
        var account = await db.PayoutAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == r.PayoutAccountId && a.UserId == user.Id && !a.Removed, ct)
            ?? throw new ChatRejectedException("Choose where to withdraw to.");
        if (r.Amount < 1m || Math.Round(r.Amount, 2) != r.Amount)
            throw new ChatRejectedException("Withdraw at least $1, to the cent.");
        var (_, onHold, withdrawable) = await billing.WithdrawableAsync(user.Id, ct);
        if (r.Amount > withdrawable)
            throw new ChatRejectedException(onHold > 0
                ? $"You can withdraw up to {CreatorsContract.Money(withdrawable)} now; {CreatorsContract.Money(onHold)} is on hold for {CreatorsContract.HoldDays} days."
                : $"You can withdraw up to {CreatorsContract.Money(withdrawable)}.");

        var withdrawal = new Withdrawal { Id = Guid.NewGuid(), UserId = user.Id, PayoutAccountId = account.Id, Amount = r.Amount, CreatedAt = Now, UpdatedAt = Now };
        db.Withdrawals.Add(withdrawal);
        await db.SaveChangesAsync(ct);

        var destination = account.Rail switch
        {
            PayoutRails.Crypto => new BridgeTransferEndpoint(profile.WalletChain!, account.Currency, ToAddress: account.Address),
            _ => new BridgeTransferEndpoint(account.Rail, account.Currency, ExternalAccountId: account.BridgeExternalAccountId)
        };
        try
        {
            var result = await bridgeClient.CreateTransferAsync(new BridgeTransferRequest(
                Amounts.Format(r.Amount),
                profile.BridgeCustomerId!,
                new BridgeTransferEndpoint("bridge_wallet", bridge.Value.Currency, BridgeWalletId: profile.WalletId),
                destination), $"withdrawal-{withdrawal.Id}", ct);
            withdrawal.BridgeTransferId = result.Id;
            Apply(withdrawal, result.State);
        }
        catch (BridgeApiException ex) when (ex.IsClientError)
        {
            log.LogWarning("Bridge refused withdrawal {WithdrawalId}: {Status} {Body}", withdrawal.Id, (int)ex.StatusCode, ex.Body);
            (withdrawal.Status, withdrawal.Error) = (PayoutStatuses.Failed, "The withdrawal was declined. Check the account and amount.");
        }
        catch (Exception ex) when (ex is BridgeApiException or HttpRequestException or TaskCanceledException)
        {
            // Unknown outcome: it stays pending and is sent again with the same idempotency key.
            log.LogWarning(ex, "Bridge didn't confirm withdrawal {WithdrawalId}; will retry", withdrawal.Id);
        }
        withdrawal.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        return new WithdrawalDto(withdrawal.Id, account.Label, withdrawal.Amount, withdrawal.Status, withdrawal.Error, withdrawal.CreatedAt);
    }

    static void Apply(Withdrawal w, string? state)
    {
        w.ProviderState = state;
        w.Status = BankingService.MapState(state) switch
        {
            TransferStatuses.Confirmed => PayoutStatuses.Paid,
            TransferStatuses.Failed => PayoutStatuses.Failed,
            _ => PayoutStatuses.Processing
        };
        if (w.Status == PayoutStatuses.Failed)
            w.Error ??= "The withdrawal didn't go through, and the money stays in your wallet.";
    }

    /// <summary>Follows withdrawals still on their way (Bridge has no webhook for them here): run on a timer.</summary>
    public async Task SyncAsync(CancellationToken ct)
    {
        var open = await db.Withdrawals.Where(w => (w.Status == PayoutStatuses.Pending || w.Status == PayoutStatuses.Processing) && w.BridgeTransferId != null)
            .Take(100).ToListAsync(ct);
        foreach (var w in open)
        {
            try
            {
                Apply(w, (await bridgeClient.GetTransferAsync(w.BridgeTransferId!, ct)).State);
                w.UpdatedAt = Now;
            }
            catch (Exception ex) when (ex is BridgeApiException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                log.LogWarning(ex, "Couldn't check withdrawal {WithdrawalId}", w.Id);
            }
        }
        await db.SaveChangesAsync(ct);
    }
}
