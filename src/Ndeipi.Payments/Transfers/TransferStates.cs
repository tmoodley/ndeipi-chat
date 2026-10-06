namespace Ndeipi.Payments.Transfers;

public enum TransferKind { UserToUser, Conversion, Onramp, Offramp }

public enum TransferState
{
    Pending, AwaitingFunds, FundsReceived, InReview, PayoutSubmitted,
    Completed, Failed, Canceled, Returned, Undeliverable, Refunded
}

/// <summary>
/// The transfer state machine (openapi.yaml, <c>TransferState</c>): which moves each kind may make.
/// Transfer services change state only through <see cref="CanMove"/>, so a transfer can never take
/// a path the contract does not document (FR-XFER-04, FR-ON-03, FR-OFF-04).
/// </summary>
public static class TransferStates
{
    static readonly Dictionary<TransferKind, Dictionary<TransferState, TransferState[]>> Moves = new()
    {
        [TransferKind.UserToUser] = new()
        {
            [TransferState.Pending] = [TransferState.Completed, TransferState.InReview, TransferState.Canceled],
            [TransferState.InReview] = [TransferState.Completed, TransferState.Failed]
        },
        // Settles from Ndeipi's own stock in one ledger transaction: it completes or fails, nothing between.
        [TransferKind.Conversion] = new()
        {
            [TransferState.Pending] = [TransferState.Completed, TransferState.Failed]
        },
        [TransferKind.Onramp] = new()
        {
            [TransferState.AwaitingFunds] = [TransferState.FundsReceived, TransferState.Canceled, TransferState.Failed],
            [TransferState.FundsReceived] = [TransferState.Completed, TransferState.InReview, TransferState.Returned, TransferState.Failed],
            [TransferState.InReview] = [TransferState.Completed, TransferState.Returned]
        },
        [TransferKind.Offramp] = new()
        {
            // The wallet is debited when the off-ramp is accepted (FR-OFF-03), so it is never canceled.
            [TransferState.Pending] = [TransferState.PayoutSubmitted, TransferState.InReview, TransferState.Failed],
            [TransferState.InReview] = [TransferState.PayoutSubmitted, TransferState.Refunded],
            [TransferState.PayoutSubmitted] = [TransferState.Completed, TransferState.Returned, TransferState.Undeliverable],
            [TransferState.Returned] = [TransferState.Refunded],
            [TransferState.Undeliverable] = [TransferState.Refunded]
        }
    };

    public static TransferState Initial(TransferKind kind) => kind == TransferKind.Onramp ? TransferState.AwaitingFunds : TransferState.Pending;

    public static bool CanMove(TransferKind kind, TransferState from, TransferState to) =>
        Moves[kind].TryGetValue(from, out var next) && next.Contains(to);

    /// <summary>States a transfer of this kind never leaves.</summary>
    public static bool IsFinal(TransferKind kind, TransferState state) =>
        Uses(kind, state) && !Moves[kind].ContainsKey(state);

    /// <summary>Whether this kind ever reaches the state.</summary>
    public static bool Uses(TransferKind kind, TransferState state) =>
        state == Initial(kind) || Moves[kind].Values.Any(next => next.Contains(state));
}
