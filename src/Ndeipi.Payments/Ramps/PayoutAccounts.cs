using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Ndeipi.Payments.Api;
using Ndeipi.Payments.Data;
using Ndeipi.Payments.Providers;
using Ndeipi.Payments.Transfers;
using Ndeipi.Payments.Users;

namespace Ndeipi.Payments.Ramps;

public sealed record BankDetailsRequest(string? BankName, string? AccountNumber, string? RoutingCode, string? SwiftBic, string? AccountType);

public sealed record MobileMoneyDetailsRequest(string? Provider, string? PhoneNumber);

public sealed record PayPalDetailsRequest(string? Email, string? Phone);

public sealed record PayoutAccountCreateRequest(
    PayoutAccountType? Type,
    string? Currency,
    string? Country,
    string? Rail,
    string? AccountOwnerName,
    BankDetailsRequest? Bank,
    MobileMoneyDetailsRequest? MobileMoney,
    PayPalDetailsRequest? Paypal,
    Dictionary<string, string>? Metadata);

/// <summary>The <c>PayoutAccount</c> object (openapi.yaml). Only masked details ever leave the server.</summary>
public sealed record PayoutAccountDto(
    string Id,
    string UserId,
    PayoutAccountType Type,
    string Currency,
    string Country,
    string Rail,
    string AccountOwnerName,
    JsonElement? Bank,
    JsonElement? MobileMoney,
    JsonElement? Paypal,
    PayoutAccountStatus Status,
    string NameCheck,
    IReadOnlyDictionary<string, string> Metadata,
    int Version,
    DateTimeOffset CreatedAt)
{
    public string Object => "payout_account";
}

/// <summary>Sanctions screening of payout beneficiaries (SRV-KYC-03).</summary>
public interface ISanctionsScreening
{
    /// <summary>True when the name matches a sanctions list.</summary>
    Task<bool> IsSanctionedAsync(string name, string country, CancellationToken ct);
}

/// <summary>Sandbox screening: any name containing "SANCTIONED" matches, so the refusal path can be tested.</summary>
public sealed class SimulatedSanctionsScreening : ISanctionsScreening
{
    public Task<bool> IsSanctionedAsync(string name, string country, CancellationToken ct) =>
        Task.FromResult(name.Contains("SANCTIONED", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Payout accounts (SRS §4.5): a user's bank account on <c>absa_eft</c> or PayPal account on
/// <c>paypal</c>. Details are validated per rail with one error per field (FR-OFF-02), the owner is
/// screened against sanctions lists (SRV-KYC-03), and the full details are encrypted at rest; only
/// the payout submission reads them back.
/// </summary>
public sealed partial class PayoutAccountService(
    PaymentsDbContext db, RampCatalog catalog, ISanctionsScreening sanctions, IDataProtectionProvider protection, TimeProvider clock)
{
    const string Purpose = "Ndeipi.Payments.PayoutDetails.v1";

    public async Task<PayoutAccountDto> CreateAsync(string userId, PayoutAccountCreateRequest r, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw PaymentsException.NotFound("user");
        TransferRules.RequireCanTransact(user, "This user");

        var errors = new List<ApiErrorDetail>();
        if (r.Type is null) errors.Add(new("type", "required", "type is required: bank or paypal."));
        if (string.IsNullOrEmpty(r.Currency)) errors.Add(new("currency", "required", "currency is required."));
        if (string.IsNullOrEmpty(r.Rail)) errors.Add(new("rail", "required", "rail is required."));
        if (r.Country is null || !CountryCode().IsMatch(r.Country)) errors.Add(new("country", "invalid_format", "country is an ISO 3166-1 alpha-2 code, like ZA."));
        if (string.IsNullOrWhiteSpace(r.AccountOwnerName) || r.AccountOwnerName.Length > 200)
            errors.Add(new("account_owner_name", "required", "account_owner_name is required, at most 200 characters."));
        UserService.ValidateMetadata(r.Metadata, errors);
        if (errors.Count > 0)
            throw PaymentsException.Validation(errors);

        var expectedType = r.Rail switch
        {
            RailCodes.AbsaEft => PayoutAccountType.Bank,
            RailCodes.PayPal => PayoutAccountType.Paypal,
            _ => (PayoutAccountType?)null
        };
        if (expectedType is null)
            throw PaymentsException.Unprocessable("unsupported_route", $"No payout route uses the rail '{r.Rail}'. See GET /v1/routes.", "rail");
        if (r.Type != expectedType)
            throw PaymentsException.Validation([new("type", "invalid_value", $"The {r.Rail} rail pays {WireEnum.Name(expectedType.Value)} accounts.")]);
        var terms = catalog.RailFor(r.Rail!, r.Currency!);
        if (terms.Country is { } railCountry && railCountry != r.Country)
            throw PaymentsException.Validation([new("country", "invalid_value", $"The {r.Rail} rail pays accounts in {railCountry}.")]);

        var (details, masked) = r.Type == PayoutAccountType.Bank ? Bank(r.Bank, errors) : PayPal(r.Paypal, errors);
        if (errors.Count > 0)
            throw PaymentsException.Validation(errors);

        if (await sanctions.IsSanctionedAsync(r.AccountOwnerName!, r.Country!, ct))
            throw PaymentsException.Unprocessable("sanctions_match", "This beneficiary cannot be paid.", "account_owner_name");

        var account = new PayoutAccount
        {
            Id = Ids.New(Ids.PayoutAccount, clock),
            UserId = userId,
            Type = r.Type!.Value,
            Currency = r.Currency!,
            Country = r.Country!,
            Rail = r.Rail!,
            AccountOwnerName = r.AccountOwnerName!.Trim(),
            ProtectedDetails = protection.CreateProtector(Purpose).Protect(JsonSerializer.Serialize(details)),
            MaskedJson = JsonSerializer.Serialize(masked, PaymentsJson.Options),
            Status = PayoutAccountStatus.Active,
            MetadataJson = JsonSerializer.Serialize(r.Metadata ?? [], PaymentsJson.Options),
            CreatedAt = clock.GetUtcNow()
        };
        db.PayoutAccounts.Add(account);
        await db.SaveChangesAsync(ct);
        return To(account);
    }

    public async Task<PayoutAccountDto> GetAsync(string userId, string id, CancellationToken ct) => To(await FindAsync(userId, id, tracked: false, ct));

    public async Task<Page<PayoutAccountDto>> ListAsync(string userId, PageRequest page, CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(u => u.Id == userId, ct))
            throw PaymentsException.NotFound("user");
        return await db.PayoutAccounts.AsNoTracking().Where(p => p.UserId == userId && p.Status == PayoutAccountStatus.Active).PageAsync(p => p.Id, page, To, ct);
    }

    /// <summary>Removes the account as a destination; refused while an off-ramp to it is still open.</summary>
    public async Task<PayoutAccountDto> DeleteAsync(string userId, string id, CancellationToken ct)
    {
        var account = await FindAsync(userId, id, tracked: true, ct);
        var open = await db.Transfers.AsNoTracking()
            .Where(t => t.PayoutAccountId == id && t.State != TransferState.Completed && t.State != TransferState.Failed && t.State != TransferState.Refunded)
            .AnyAsync(ct);
        if (open)
            throw PaymentsException.Conflict("payout_account_in_use", "An off-ramp to this account is still open.");
        account.Status = PayoutAccountStatus.Deleted;
        account.Version++;
        await db.SaveChangesAsync(ct);
        return To(account);
    }

    /// <summary>The full destination details, for submitting a payout. Nothing else reads them.</summary>
    public IReadOnlyDictionary<string, string> Destination(PayoutAccount account) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(protection.CreateProtector(Purpose).Unprotect(account.ProtectedDetails))!;

    public async Task<PayoutAccount> FindAsync(string userId, string id, bool tracked, CancellationToken ct)
    {
        var query = tracked ? db.PayoutAccounts : db.PayoutAccounts.AsNoTracking();
        return await query.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId && p.Status == PayoutAccountStatus.Active, ct)
            ?? throw PaymentsException.NotFound("payout account");
    }

    static (Dictionary<string, string> Details, object Masked) Bank(BankDetailsRequest? bank, List<ApiErrorDetail> errors)
    {
        if (bank is null)
        {
            errors.Add(new("bank", "required", "bank is required for bank accounts."));
            return ([], new { });
        }
        if (string.IsNullOrWhiteSpace(bank.BankName)) errors.Add(new("bank.bank_name", "required", "bank.bank_name is required."));
        var number = bank.AccountNumber?.Replace(" ", "");
        if (number is null || !AccountNumber().IsMatch(number)) errors.Add(new("bank.account_number", "invalid_format", "bank.account_number is 6 to 20 digits."));
        if (bank.RoutingCode is not null && !RoutingCode().IsMatch(bank.RoutingCode)) errors.Add(new("bank.routing_code", "invalid_format", "bank.routing_code is a 6-digit branch code."));
        if (bank.AccountType is not (null or "checking" or "savings" or "other")) errors.Add(new("bank.account_type", "invalid_value", "bank.account_type is checking, savings or other."));

        var details = new Dictionary<string, string> { ["bank_name"] = bank.BankName ?? "", ["account_number"] = number ?? "" };
        if (bank.RoutingCode is not null) details["routing_code"] = bank.RoutingCode;
        if (bank.SwiftBic is not null) details["swift_bic"] = bank.SwiftBic;
        if (bank.AccountType is not null) details["account_type"] = bank.AccountType;
        return (details, new
        {
            bank_name = bank.BankName,
            account_number_last4 = number is { Length: >= 4 } ? number[^4..] : null,
            routing_code = bank.RoutingCode,
            swift_bic = bank.SwiftBic,
            account_type = bank.AccountType
        });
    }

    static (Dictionary<string, string> Details, object Masked) PayPal(PayPalDetailsRequest? paypal, List<ApiErrorDetail> errors)
    {
        if (paypal is null || (paypal.Email is null) == (paypal.Phone is null))
        {
            errors.Add(new("paypal", "required", "paypal needs exactly one of email or phone."));
            return ([], new { });
        }
        if (paypal.Email is not null && !Email().IsMatch(paypal.Email)) errors.Add(new("paypal.email", "invalid_format", "paypal.email is not a valid address."));
        if (paypal.Phone is not null && !E164().IsMatch(paypal.Phone)) errors.Add(new("paypal.phone", "invalid_format", "paypal.phone is in E.164 form."));

        return paypal.Email is { } email
            ? (new() { ["email"] = email }, new { email_masked = MaskEmail(email) })
            : (new() { ["phone"] = paypal.Phone! }, new { phone_number_last4 = paypal.Phone![^4..] });
    }

    /// <summary>"alice@example.com" as "a***e@example.com".</summary>
    static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        var local = email[..at];
        return (local.Length <= 2 ? local[..1] + "***" : $"{local[0]}***{local[^1]}") + email[at..];
    }

    static PayoutAccountDto To(PayoutAccount p)
    {
        var masked = JsonDocument.Parse(p.MaskedJson).RootElement.Clone();
        return new(p.Id, p.UserId, p.Type, p.Currency, p.Country, p.Rail, p.AccountOwnerName,
            p.Type == PayoutAccountType.Bank ? masked : null,
            p.Type == PayoutAccountType.MobileMoney ? masked : null,
            p.Type == PayoutAccountType.Paypal ? masked : null,
            p.Status, "not_supported",
            JsonSerializer.Deserialize<Dictionary<string, string>>(p.MetadataJson, PaymentsJson.Options) ?? [], p.Version, p.CreatedAt);
    }

    [GeneratedRegex("^[A-Z]{2}$")] private static partial Regex CountryCode();
    [GeneratedRegex(@"^\d{6,20}$")] private static partial Regex AccountNumber();
    [GeneratedRegex(@"^\d{6}$")] private static partial Regex RoutingCode();
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")] private static partial Regex Email();
    [GeneratedRegex(@"^\+[1-9]\d{6,14}$")] private static partial Regex E164();
}
