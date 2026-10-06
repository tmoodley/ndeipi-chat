using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ndeipi.Payments.Api;
using Ndeipi.Payments.Data;
using Ndeipi.Payments.Providers;
using Ndeipi.Payments.Transfers;

namespace Ndeipi.Payments.Ramps;

/// <summary>
/// Moves ramps along without a request to drive them:
///
/// - submits accepted off-ramps to their rail, retrying while the provider is down (SRV-PROV-07);
/// - asks the rail how submitted payouts ended, where it has no webhook (SRV-PROV-05);
/// - cancels one-off on-ramps whose deposit instructions expired unpaid.
///
/// Each transfer is handled in its own scope, acting as the integrator it belongs to. Several
/// instances can run this at once: state changes are guarded by the transfer's version, and a
/// payout's provider reference is the transfer's ID, so a repeated submission is not paid twice.
/// </summary>
public sealed class RampWorker(IServiceScopeFactory scopes, IOptions<PaymentsOptions> options, TimeProvider clock, ILogger<RampWorker> log) : BackgroundService
{
    const int Batch = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                log.LogError(e, "Ramp worker pass failed.");
            }
            await Task.Delay(options.Value.RampPollInterval, clock, stoppingToken);
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        List<(string Id, Guid Integrator, TransferState State, string? Rail)> due;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var now = clock.GetUtcNow();
            var transfers = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Transfers.IgnoreQueryFilters().AsNoTracking();
            var offramps = await transfers
                .Where(t => t.Kind == TransferKind.Offramp && (t.State == TransferState.Pending || t.State == TransferState.PayoutSubmitted))
                .OrderBy(t => t.UpdatedAt).Take(Batch)
                .Select(t => new { t.Id, t.IntegratorId, t.State, t.Rail }).ToListAsync(ct);
            var expired = await transfers
                .Where(t => t.Kind == TransferKind.Onramp && t.State == TransferState.AwaitingFunds && t.ExpiresAt <= now)
                .OrderBy(t => t.ExpiresAt).Take(Batch)
                .Select(t => new { t.Id, t.IntegratorId, t.State, t.Rail }).ToListAsync(ct);
            due = [.. offramps.Concat(expired).Select(t => (t.Id, t.IntegratorId, t.State, t.Rail))];
        }

        foreach (var (id, integrator, state, rail) in due)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<IntegratorScope>().Set(integrator, apiKeyId: null);
                var services = scope.ServiceProvider;
                switch (state)
                {
                    case TransferState.Pending:
                        await services.GetRequiredService<OffRampService>().SubmitAsync(id, ct);
                        break;
                    case TransferState.PayoutSubmitted:
                        var status = await services.GetRequiredService<ProviderRegistry>().RailFor(rail!).GetStatusAsync(id, ct);
                        if (status is { Status: ProviderOperationStatus.Completed or ProviderOperationStatus.Returned or ProviderOperationStatus.Undeliverable })
                            await services.GetRequiredService<OffRampService>().ApplyOutcomeAsync(id, status.Status, status.RailReference, status.Reason, ct);
                        break;
                    case TransferState.AwaitingFunds:
                        await services.GetRequiredService<OnRampService>().ExpireAsync(id, ct);
                        break;
                }
            }
            catch (ProviderUnavailableException)
            {
                // Tried again next pass.
            }
            catch (PaymentsException e) when (e.Error.Code == "invalid_state")
            {
                // Moved on by a request or another instance in the meantime.
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.LogError(e, "Ramp worker could not move transfer {TransferId}.", id);
            }
        }
    }
}
