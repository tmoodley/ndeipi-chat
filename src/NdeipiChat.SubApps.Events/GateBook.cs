using NdeipiChat.Contracts;
using NdeipiChat.SubApps.Sdk;

namespace NdeipiChat.SubApps.Events;

public enum GateVerdict { Admit, AlreadyAdmitted, Expired, NotActivated, Forged, Revoked, Unknown, NotATicket }

public sealed record GateScan(GateVerdict Verdict, string Message, GateTicketDto? Ticket, DateTimeOffset? AdmittedAt);

/// <summary>
/// A gate's view of one event, kept on the device so scanning works with no signal (SRS FR-5.2):
/// the synced ticket list (public keys only), who's been let in, and admissions waiting to upload.
/// A code is good if it's for a ticket on the list, for a window within
/// <see cref="EventsContract.QrWindowTolerance"/> of now, and signed by the holder's registered key.
/// </summary>
public sealed class GateBook(Guid eventId, ISubAppDevice device)
{
    GateListDto? _list;
    Dictionary<Guid, GateTicketDto> _tickets = [];
    Dictionary<Guid, DateTimeOffset> _admitted = [];
    List<AdmissionDto> _pending = [];

    string Name(string part) => $"gate.{eventId:N}.{part}";

    public GateListDto? List => _list;
    public int TicketCount => _tickets.Count;
    public int AdmittedCount => _admitted.Count;
    public IReadOnlyList<AdmissionDto> Pending => _pending;

    public async Task LoadAsync()
    {
        if (await device.GetValueAsync(Name("list")) is { } list && ContractJson.Read<GateListDto>(list) is { } parsed)
            Index(parsed);
        _admitted = await device.GetValueAsync(Name("admitted")) is { } admitted ? ContractJson.Read<Dictionary<Guid, DateTimeOffset>>(admitted) ?? [] : [];
        _pending = await device.GetValueAsync(Name("pending")) is { } pending ? ContractJson.Read<List<AdmissionDto>>(pending) ?? [] : [];
    }

    /// <summary>Takes a fresh list from the server, keeping this gate's own admissions.</summary>
    public async Task ApplyListAsync(GateListDto list)
    {
        Index(list);
        foreach (var t in list.Tickets.Where(t => t.Status == TicketStatuses.Admitted))
            _admitted.TryAdd(t.TicketId, t.AdmittedAt ?? list.SyncedAt);
        await device.SetValueAsync(Name("list"), ContractJson.Write(list));
        await SaveAdmittedAsync();
    }

    void Index(GateListDto list)
    {
        _list = list;
        _tickets = list.Tickets.ToDictionary(t => t.TicketId);
    }

    public async Task<GateScan> CheckAsync(string? code, DateTimeOffset now)
    {
        if (!EventsContract.TryParseQr(code, out var ticketId, out var window, out var signature))
            return new(GateVerdict.NotATicket, "That isn't an Ndeipi ticket.", null, null);
        if (!_tickets.TryGetValue(ticketId, out var ticket))
            return new(GateVerdict.Unknown, _list is null ? "Sync the ticket list first." : "Not a ticket for this event.", null, null);
        if (ticket.Status == TicketStatuses.Revoked)
            return new(GateVerdict.Revoked, "This ticket was cancelled.", ticket, null);
        if (Math.Abs(window - EventsContract.WindowAt(now)) > EventsContract.QrWindowTolerance)
            return new(GateVerdict.Expired, "Old code. Ask them to open the ticket again, or check their phone's clock.", ticket, null);
        if (ticket.HolderPublicKey is null)
            return new(GateVerdict.NotActivated, "This ticket hasn't been opened on a phone yet. Ask them to open it, then sync.", ticket, null);
        if (!await device.VerifyAsync(ticket.HolderPublicKey, EventsContract.QrPayload(ticketId, window), signature))
            return new(GateVerdict.Forged, "This code wasn't made by the ticket holder's phone.", ticket, null);
        if (_admitted.TryGetValue(ticketId, out var at))
            return new(GateVerdict.AlreadyAdmitted, $"Already let in at {Format.Time(at)}.", ticket, at);

        _admitted[ticketId] = now;
        _pending.Add(new AdmissionDto(ticketId, now));
        await SaveAdmittedAsync();
        await device.SetValueAsync(Name("pending"), ContractJson.Write(_pending));
        return new(GateVerdict.Admit, $"{ticket.TierName} · {ticket.HolderName}", ticket, now);
    }

    /// <summary>
    /// Sends admissions made here to the server. Tickets another gate let in first come back as
    /// conflicts, with the server's time.
    /// </summary>
    public async Task<IReadOnlyList<AdmissionResultDto>> UploadAsync(Func<AdmissionsRequest, Task<IReadOnlyList<AdmissionResultDto>>> send)
    {
        if (_pending.Count == 0)
            return [];
        var batch = _pending.Take(500).ToList();
        var results = await send(new AdmissionsRequest(batch, device.DeviceName));
        var sent = batch.Select(a => a.TicketId).ToHashSet();
        _pending.RemoveAll(a => sent.Contains(a.TicketId));
        foreach (var r in results.Where(r => r.FirstAdmittedAt is not null))
            _admitted[r.TicketId] = r.FirstAdmittedAt!.Value;
        await device.SetValueAsync(Name("pending"), ContractJson.Write(_pending));
        await SaveAdmittedAsync();
        return results.Where(r => r.Outcome != AdmissionOutcomes.Admitted).ToList();
    }

    Task SaveAdmittedAsync() => device.SetValueAsync(Name("admitted"), ContractJson.Write(_admitted));
}
