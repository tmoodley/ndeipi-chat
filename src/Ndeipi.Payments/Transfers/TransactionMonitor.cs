using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ndeipi.Payments.Api;
using Ndeipi.Payments.Data;
using Ndeipi.Payments.Users;

namespace Ndeipi.Payments.Transfers;

/// <summary>
/// Transaction monitoring (SRV-KYC-04): decides whether an outgoing transfer of points is held in
/// <c>in_review</c> until an operator releases or rejects it (SRV-OPS-04). Two rules, set by
/// compliance in <c>Payments:Monitoring</c>: a single amount, and the sender's outgoing total over the
/// last 24 hours. User-to-user transfers and cash-outs are checked; deposits are held when the rail or
/// sanctions screening says so.
/// </summary>
public sealed class TransactionMonitor(PaymentsDbContext db, IOptions<PaymentsOptions> options, TimeProvider clock)
{
    static readonly TransferState[] NotCounted = [TransferState.Failed, TransferState.Canceled, TransferState.Refunded];

    /// <summary>Why this transfer is held, or null to let it through.</summary>
    public async Task<ReasonDto?> AssessAsync(string senderUserId, string asset, decimal amount, CancellationToken ct)
    {
        var rules = options.Value.Monitoring;
        if (asset != options.Value.Points.Asset)
            return null;

        if (rules.ReviewSingleFrom > 0 && amount >= rules.ReviewSingleFrom)
            return new ReasonDto("large_amount", "Transfers of this size are reviewed before they complete.");

        if (rules.ReviewDailyFrom > 0)
        {
            var since = clock.GetUtcNow().AddHours(-24);
            var sent = await db.Transfers.AsNoTracking()
                .Where(t => t.SourceUserId == senderUserId && t.Asset == asset && t.CreatedAt > since &&
                            (t.Kind == TransferKind.UserToUser || t.Kind == TransferKind.Offramp) && !NotCounted.Contains(t.State))
                .SumAsync(t => (decimal?)t.Amount, ct) ?? 0m;
            if (sent + amount >= rules.ReviewDailyFrom)
                return new ReasonDto("daily_volume", "This sender's transfers in the last 24 hours are reviewed before more complete.");
        }
        return null;
    }
}
