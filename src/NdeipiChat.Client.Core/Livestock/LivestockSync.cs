using NdeipiChat.Client.Realtime;
using NdeipiChat.Contracts;

namespace NdeipiChat.Client.Livestock;

/// <param name="Capture">The capture's state after the attempt; gone from the queue if it was accepted.</param>
/// <param name="Response">The server's answer, when there was one.</param>
public sealed record UploadOutcome(PendingCapture Capture, LivestockRegistrationResponse? Response, bool Accepted);

/// <summary>
/// Sends queued captures: signs each with the operator's device key at send time, uploads it, and
/// records the answer. Accepted captures leave the phone; refused ones wait for the farmer; anything
/// that failed for want of a connection or a working server waits for the next try.
/// </summary>
public sealed class LivestockSync(LivestockCaptureQueue queue, LivestockApi api, OperatorSigner signer)
{
    public const string OfflineMessage = "Waiting for a connection. It will upload automatically.";

    readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Raised after every upload attempt.</summary>
    public event Action<UploadOutcome>? CaptureUploaded;

    public async Task<UploadOutcome> UploadAsync(Guid captureId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await UploadCoreAsync(captureId, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Uploads everything waiting, oldest first, stopping at the first sign of being offline.</summary>
    public async Task<int> UploadPendingAsync(CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct))
            return 0; // already running
        try
        {
            var accepted = 0;
            foreach (var capture in (await queue.ListAsync(ct)).Where(c => c.Status == CaptureStatuses.Pending))
            {
                var outcome = await UploadCoreAsync(capture.Id, ct);
                if (outcome.Accepted)
                    accepted++;
                else if (outcome.Capture.LastError == OfflineMessage)
                    break;
            }
            return accepted;
        }
        finally
        {
            _gate.Release();
        }
    }

    async Task<UploadOutcome> UploadCoreAsync(Guid captureId, CancellationToken ct)
    {
        var capture = await queue.GetAsync(captureId, ct) ?? throw new InvalidOperationException($"No queued capture {captureId}.");
        if (capture.Status != CaptureStatuses.Pending)
            return new UploadOutcome(capture, null, false);

        var face = await File.ReadAllBytesAsync(capture.FacePath, ct);
        var flank = await File.ReadAllBytesAsync(capture.FlankPath, ct);
        var payload = LivestockContract.SigningPayload(face, flank, capture.MetadataJson);

        UploadOutcome outcome;
        try
        {
            var result = await SubmitAsync(face, flank, capture, payload, ct);
            if (result.StatusCode == 403)
            {
                // The server no longer knows this device's key (revoked, or the account's keys were
                // reset). Register a fresh one and try once more.
                await signer.ForgetKeyAsync();
                result = await SubmitAsync(face, flank, capture, payload, ct);
            }
            outcome = await RecordAsync(capture, result, ct);
        }
        catch (ApiException ex) when (ex.StatusCode is null)
        {
            var waiting = capture with { Attempts = capture.Attempts + 1, LastError = OfflineMessage };
            await queue.UpdateAsync(waiting, ct);
            outcome = new UploadOutcome(waiting, null, false);
        }

        CaptureUploaded?.Invoke(outcome);
        return outcome;
    }

    async Task<RegistrationResult> SubmitAsync(byte[] face, byte[] flank, PendingCapture capture, byte[] payload, CancellationToken ct)
    {
        var (keyId, signature) = await signer.SignAsync(payload, ct);
        return await api.RegisterAsync(face, flank, capture.MetadataJson, keyId, signature, ct);
    }

    async Task<UploadOutcome> RecordAsync(PendingCapture capture, RegistrationResult result, CancellationToken ct)
    {
        if (result.IsSuccess)
        {
            await queue.RemoveAsync(capture.Id, ct);
            return new UploadOutcome(capture with { LastError = null }, result.Response, true);
        }

        var updated = capture with
        {
            Status = result.IsRetryable ? CaptureStatuses.Pending : CaptureStatuses.Rejected,
            Attempts = capture.Attempts + 1,
            LastError = result.ProblemText
        };
        await queue.UpdateAsync(updated, ct);
        return new UploadOutcome(updated, result.Response, false);
    }
}
