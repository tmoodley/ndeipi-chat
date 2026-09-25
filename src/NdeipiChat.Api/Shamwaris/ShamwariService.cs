using System.Net.Mail;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Shamwaris;

/// <summary>
/// Shamwari requests and friendships. Each change is pushed to the other person as
/// <see cref="IChatClient.ShamwarisChanged"/>, and their app reloads its list.
/// </summary>
public sealed class ShamwariService(ChatDbContext db, ChatNotifier notifier, TimeProvider clock, ILogger<ShamwariService> log)
{
    /// <summary>Caps unanswered requests and invites, so the endpoint can't be used to probe or spam.</summary>
    public const int MaxPendingOutgoing = 100;

    public async Task<ShamwariListDto> ListAsync(Guid me, CancellationToken ct)
    {
        var links = await db.Shamwaris.AsNoTracking()
            .Include(s => s.Requester)
            .Include(s => s.Addressee)
            .Where(s => s.RequesterId == me || s.AddresseeId == me)
            .ToListAsync(ct);

        var shamwaris = links
            .Where(l => l.Accepted)
            .Select(l => new ShamwariDto(ChatMapper.ToDto(l.RequesterId == me ? l.Addressee! : l.Requester), l.AcceptedAt ?? l.CreatedAt))
            .OrderBy(s => s.User.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var incoming = links
            .Where(l => !l.Accepted && l.AddresseeId == me)
            .OrderByDescending(l => l.CreatedAt)
            .Select(l => new ShamwariRequestDto(l.Id, ChatMapper.ToDto(l.Requester), null, l.CreatedAt))
            .ToList();
        var outgoing = links
            .Where(l => !l.Accepted && l.RequesterId == me)
            .OrderByDescending(l => l.CreatedAt)
            .Select(l => new ShamwariRequestDto(l.Id, l.Addressee is null ? null : ChatMapper.ToDto(l.Addressee), l.InviteContact, l.CreatedAt))
            .ToList();
        return new ShamwariListDto(shamwaris, incoming, outgoing);
    }

    public async Task<AddShamwariResponse> AddAsync(User me, string? contact, CancellationToken ct)
    {
        var normalized = ShamwariContact.Normalize(contact);
        var target = ShamwariContact.IsEmail(normalized)
            ? await db.Users.FirstOrDefaultAsync(u => u.EmailVerified && u.Email == normalized, ct)
            : await db.Users.FirstOrDefaultAsync(u => u.Phone == normalized, ct);
        if (target?.Id == me.Id)
            throw new ChatRejectedException("That's you.");

        var outcome = target is null ? await InviteAsync(me, normalized, ct) : await RequestAsync(me, target, ct);
        return new AddShamwariResponse(outcome, await ListAsync(me.Id, ct));
    }

    async Task<ShamwariAddOutcome> RequestAsync(User me, User target, CancellationToken ct)
    {
        var key = PairKey(me.Id, target.Id);
        var link = await db.Shamwaris.FirstOrDefaultAsync(s => s.PairKey == key, ct);
        if (link is { Accepted: true })
            return ShamwariAddOutcome.AlreadyShamwaris;
        if (link is not null && link.RequesterId == me.Id)
            return ShamwariAddOutcome.AlreadyRequested;

        if (link is not null)
        {
            // They'd already asked; adding them back is as good as accepting.
            (link.Accepted, link.AcceptedAt) = (true, clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
            await notifier.ToUser(target.ClerkUserId).ShamwarisChanged();
            return ShamwariAddOutcome.NowShamwaris;
        }

        await EnsureUnderLimitAsync(me.Id, ct);
        db.Shamwaris.Add(new ShamwariLink
        {
            Id = Guid.NewGuid(),
            RequesterId = me.Id,
            AddresseeId = target.Id,
            PairKey = key,
            CreatedAt = clock.GetUtcNow()
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // They added us at the same moment. Their link is there now, so look again.
            db.ChangeTracker.Clear();
            return await RequestAsync(me, target, ct);
        }

        await notifier.ToUser(target.ClerkUserId).ShamwarisChanged();
        return ShamwariAddOutcome.RequestSent;
    }

    async Task<ShamwariAddOutcome> InviteAsync(User me, string contact, CancellationToken ct)
    {
        if (await db.Shamwaris.AnyAsync(s => s.RequesterId == me.Id && s.InviteContact == contact, ct))
            return ShamwariAddOutcome.AlreadyRequested;

        await EnsureUnderLimitAsync(me.Id, ct);
        db.Shamwaris.Add(new ShamwariLink
        {
            Id = Guid.NewGuid(),
            RequesterId = me.Id,
            InviteContact = contact,
            CreatedAt = clock.GetUtcNow()
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return ShamwariAddOutcome.AlreadyRequested;
        }
        return ShamwariAddOutcome.Invited;
    }

    async Task EnsureUnderLimitAsync(Guid me, CancellationToken ct)
    {
        if (await db.Shamwaris.CountAsync(s => s.RequesterId == me && !s.Accepted, ct) >= MaxPendingOutgoing)
            throw new ChatRejectedException("You have too many unanswered requests. Cancel some before adding more.");
    }

    public async Task<ShamwariListDto> AcceptAsync(User me, Guid requestId, CancellationToken ct)
    {
        var link = await db.Shamwaris.Include(s => s.Requester).FirstOrDefaultAsync(s => s.Id == requestId && s.AddresseeId == me.Id, ct)
            ?? throw new ChatRejectedException("That request isn't there any more.");
        if (!link.Accepted)
        {
            (link.Accepted, link.AcceptedAt) = (true, clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
            await notifier.ToUser(link.Requester.ClerkUserId).ShamwarisChanged();
        }
        return await ListAsync(me.Id, ct);
    }

    /// <summary>Declines a request to you, or cancels one you sent -- invites included.</summary>
    public async Task<ShamwariListDto> DeleteRequestAsync(User me, Guid requestId, CancellationToken ct)
    {
        var link = await db.Shamwaris
            .Include(s => s.Requester)
            .Include(s => s.Addressee)
            .FirstOrDefaultAsync(s => s.Id == requestId && !s.Accepted && (s.RequesterId == me.Id || s.AddresseeId == me.Id), ct);
        if (link is not null)
            await RemoveLinkAsync(me, link, ct);
        return await ListAsync(me.Id, ct);
    }

    public async Task<ShamwariListDto> RemoveAsync(User me, Guid userId, CancellationToken ct)
    {
        var key = PairKey(me.Id, userId);
        var link = await db.Shamwaris
            .Include(s => s.Requester)
            .Include(s => s.Addressee)
            .FirstOrDefaultAsync(s => s.PairKey == key && s.Accepted, ct);
        if (link is not null)
            await RemoveLinkAsync(me, link, ct);
        return await ListAsync(me.Id, ct);
    }

    async Task RemoveLinkAsync(User me, ShamwariLink link, CancellationToken ct)
    {
        db.Shamwaris.Remove(link);
        await db.SaveChangesAsync(ct);
        var other = link.RequesterId == me.Id ? link.Addressee : link.Requester;
        if (other is not null)
            await notifier.ToUser(other.ClerkUserId).ShamwarisChanged();
    }

    /// <summary>
    /// Turns invites sent to this user's verified email or phone number into requests to them.
    /// Runs whenever their profile is synced from Clerk, so it also catches a number added later.
    /// </summary>
    public async Task ClaimInvitesAsync(User user, CancellationToken ct)
    {
        var contacts = new List<string>();
        if (user is { EmailVerified: true, Email: { } email })
            contacts.Add(email.ToLowerInvariant());
        if (user.Phone is { } phone)
            contacts.Add(phone);
        if (contacts.Count == 0)
            return;

        var invites = await db.Shamwaris
            .Include(s => s.Requester)
            .Where(s => s.InviteContact != null && contacts.Contains(s.InviteContact))
            .ToListAsync(ct);
        if (invites.Count == 0)
            return;

        var keys = invites.Select(i => PairKey(i.RequesterId, user.Id)).ToList();
        var taken = (await db.Shamwaris.Where(s => s.PairKey != null && keys.Contains(s.PairKey)).Select(s => s.PairKey!).ToListAsync(ct)).ToHashSet();
        var claimed = new List<ShamwariLink>();
        foreach (var invite in invites)
        {
            var key = PairKey(invite.RequesterId, user.Id);
            if (invite.RequesterId == user.Id || !taken.Add(key))
            {
                // Invited themselves, or the two are already linked (or invited each other twice).
                db.Shamwaris.Remove(invite);
                continue;
            }
            (invite.AddresseeId, invite.InviteContact, invite.PairKey) = (user.Id, null, key);
            claimed.Add(invite);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // A request between the same two people landed meanwhile; the next profile sync retries.
            log.LogWarning(ex, "Couldn't claim Shamwari invites for {UserId}", user.Id);
            db.ChangeTracker.Clear();
            return;
        }

        if (claimed.Count > 0)
        {
            await notifier.ToClerkIds(claimed.Select(c => c.Requester.ClerkUserId)).ShamwarisChanged();
            await notifier.ToUser(user.ClerkUserId).ShamwarisChanged();
        }
    }

    static string PairKey(Guid a, Guid b) => a.CompareTo(b) < 0 ? $"{a:N}:{b:N}" : $"{b:N}:{a:N}";
}

/// <summary>What a Shamwari can be added by: a lower-case email, or a phone number in E.164 form.</summary>
public static class ShamwariContact
{
    public static bool IsEmail(string normalized) => normalized.Contains('@');

    /// <summary>Normalizes what the user typed, or throws a refusal they can read.</summary>
    public static string Normalize(string? input)
    {
        var text = input?.Trim() ?? "";
        if (text.Length == 0)
            throw new ChatRejectedException("Enter an email address or a phone number.");

        if (text.Contains('@'))
        {
            var email = text.ToLowerInvariant();
            if (email.Length > 320 || !MailAddress.TryCreate(email, out var parsed) || parsed.Address != email)
                throw new ChatRejectedException("That doesn't look like an email address.");
            return email;
        }

        return NormalizePhone(text) ?? throw new ChatRejectedException("Enter the number with its country code, e.g. +263 77 123 4567.");
    }

    /// <summary>E.164 (+ and 8 to 15 digits), accepting spaces, dashes, dots, brackets and a 00 prefix.</summary>
    public static string? NormalizePhone(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 40)
            return null;

        var plus = false;
        var digits = new StringBuilder();
        foreach (var c in text.Trim())
        {
            if (char.IsAsciiDigit(c))
                digits.Append(c);
            else if (c == '+' && !plus && digits.Length == 0)
                plus = true;
            else if (c is not (' ' or '-' or '.' or '(' or ')'))
                return null;
        }

        if (!plus && digits.Length > 2 && digits[0] == '0' && digits[1] == '0')
        {
            digits.Remove(0, 2);
            plus = true;
        }
        return plus && digits.Length is >= 8 and <= 15 && digits[0] != '0' ? "+" + digits : null;
    }
}
