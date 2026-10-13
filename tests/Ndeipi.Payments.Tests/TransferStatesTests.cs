using Ndeipi.Payments.Transfers;

namespace Ndeipi.Payments.Tests;

/// <summary>The transfer state machine as openapi.yaml documents it (FR-XFER-04, FR-ON-03, FR-OFF-04, FR-OFF-05).</summary>
public sealed class TransferStatesTests
{
    [Theory]
    [InlineData(TransferKind.UserToUser, TransferState.Pending)]
    [InlineData(TransferKind.Offramp, TransferState.Pending)]
    [InlineData(TransferKind.Onramp, TransferState.AwaitingFunds)]
    public void Each_kind_starts_where_the_contract_says(TransferKind kind, TransferState initial) =>
        Assert.Equal(initial, TransferStates.Initial(kind));

    [Fact]
    public void An_off_ramp_whose_payout_fails_can_only_end_refunded()
    {
        foreach (var failed in new[] { TransferState.Returned, TransferState.Undeliverable })
        {
            Assert.True(TransferStates.CanMove(TransferKind.Offramp, TransferState.PayoutSubmitted, failed));
            Assert.True(TransferStates.CanMove(TransferKind.Offramp, failed, TransferState.Refunded));
            Assert.False(TransferStates.IsFinal(TransferKind.Offramp, failed));
        }
        Assert.True(TransferStates.IsFinal(TransferKind.Offramp, TransferState.Refunded));
    }

    [Fact]
    public void An_on_ramp_can_be_canceled_only_while_awaiting_funds()
    {
        Assert.True(TransferStates.CanMove(TransferKind.Onramp, TransferState.AwaitingFunds, TransferState.Canceled));
        Assert.False(TransferStates.CanMove(TransferKind.Onramp, TransferState.FundsReceived, TransferState.Canceled));
        Assert.False(TransferStates.CanMove(TransferKind.Offramp, TransferState.Pending, TransferState.Canceled));
    }

    [Fact]
    public void Final_states_have_no_way_out()
    {
        foreach (var kind in Enum.GetValues<TransferKind>())
            foreach (var state in Enum.GetValues<TransferState>().Where(s => TransferStates.IsFinal(kind, s)))
                Assert.DoesNotContain(Enum.GetValues<TransferState>(), next => TransferStates.CanMove(kind, state, next));
    }

    [Fact]
    public void A_user_to_user_transfer_never_touches_ramp_states()
    {
        foreach (var state in new[] { TransferState.AwaitingFunds, TransferState.FundsReceived, TransferState.PayoutSubmitted, TransferState.Returned, TransferState.Undeliverable, TransferState.Refunded })
            Assert.False(TransferStates.Uses(TransferKind.UserToUser, state));
    }
}
