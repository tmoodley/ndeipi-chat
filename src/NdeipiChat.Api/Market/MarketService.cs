using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Market;

/// <summary>
/// "market.listing" messages (SRS §3.4): livestock for sale, posted in a chat. A new listing is
/// stored for the Market app; forwarding one posts the same listing in another chat, so its status
/// stays the same everywhere it's shown.
/// </summary>
public sealed class ListingMessageHandler(ChatDbContext db, ChatMediaService media, TimeProvider clock) : IMessageKindHandler
{
    public string Kind => MessageKinds.MarketListing;

    public async Task<PreparedMessage> PrepareAsync(MessageContext context, JsonElement payload, CancellationToken ct)
    {
        var sent = MessagePayload.Read<MarketListingPayload>(payload);
        MarketListing listing;
        if (sent.ListingId != Guid.Empty)
        {
            listing = await db.MarketListings.FirstOrDefaultAsync(l => l.Id == sent.ListingId, ct)
                ?? throw new ChatRejectedException("That listing isn't there any more.");
            var visible = listing.SellerId == context.Sender.Id || await db.MarketListingPosts.AnyAsync(p => p.ListingId == listing.Id
                && db.Members.Any(m => m.ConversationId == p.ConversationId && m.UserId == context.Sender.Id), ct);
            if (!visible)
                throw new ChatRejectedException("That listing isn't there any more.");
        }
        else
        {
            listing = await NewAsync(context, sent, ct);
        }

        db.MarketListingPosts.Add(new MarketListingPost
        {
            MessageId = context.MessageId,
            ListingId = listing.Id,
            ConversationId = context.Conversation.Id,
            PostedAt = clock.GetUtcNow()
        });
        var items = await MarketService.MediaOfAsync(db, listing, ct);
        return new PreparedMessage(ContractJson.ToElement(MarketService.ToPayload(listing, items, sent.ListingId != Guid.Empty && sent.Forwarded)),
            new ListingState(listing.Status));
    }

    async Task<MarketListing> NewAsync(MessageContext context, MarketListingPayload p, CancellationToken ct)
    {
        var title = Clean(p.Title, MarketContract.MaxTitleLength) ?? throw new ChatRejectedException("Give the listing a title, e.g. \"Boer goat buck\".");
        if (!MarketContract.Species.Any(s => s.Code == p.Species))
            throw new ChatRejectedException("Choose the kind of animal.");
        if (p.Quantity is < 1 or > 100_000)
            throw new ChatRejectedException("How many? Between 1 and 100,000.");
        if (p.Price is { } price && (price <= 0 || price > 1_000_000_000))
            throw new ChatRejectedException("Enter a price above zero, or leave it empty for \"price on request\".");
        var currency = string.IsNullOrWhiteSpace(p.Currency) ? MarketContract.DefaultCurrency : p.Currency.Trim().ToUpperInvariant();
        if (!MarketContract.Currencies.Contains(currency))
            throw new ChatRejectedException("Prices can be in kwacha (ZMW) or US dollars (USD).");
        var status = string.IsNullOrEmpty(p.Status) ? ListingStatuses.Available : p.Status;
        if (status is not (ListingStatuses.Available or ListingStatuses.Upcoming))
            throw new ChatRejectedException("A new listing is for sale now, or coming soon.");
        var description = Clean(p.Description, MarketContract.MaxDescriptionLength);
        var barter = p.OpenToBarter ? Clean(p.BarterTerms, 200) : null;
        var items = await media.ClaimAsync(context, (p.Media ?? []).Select(m => m.Id).ToList(), forwarded: false, ct);

        var now = clock.GetUtcNow();
        var listing = new MarketListing
        {
            Id = Guid.NewGuid(),
            SellerId = context.Sender.Id,
            Title = title,
            Species = p.Species,
            Breed = Clean(p.Breed, 60),
            Quantity = p.Quantity,
            Price = p.Price is { } a ? Math.Round(a, 2) : null,
            Currency = currency,
            Location = Clean(p.Location, 80),
            Age = Clean(p.Age, 40),
            Description = description,
            OpenToBarter = p.OpenToBarter,
            BarterTerms = barter,
            Status = status,
            MediaIds = string.Join(',', items.Select(i => i.Id.ToString("N"))),
            Tags = MarketService.TagField(Hashtags.Find(title, description, barter)),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.MarketListings.Add(listing);
        return listing;
    }

    internal static string? Clean(string? value, int max)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;
        return trimmed.Length > max ? throw new ChatRejectedException($"Keep it under {max} characters.") : trimmed;
    }
}

/// <summary>"market.offer" messages: an offer on a listing in the same chat, cash or a barter (SRS §3.4).</summary>
public sealed class OfferMessageHandler(ChatDbContext db, TimeProvider clock) : IMessageKindHandler
{
    public string Kind => MessageKinds.MarketOffer;

    public async Task<PreparedMessage> PrepareAsync(MessageContext context, JsonElement payload, CancellationToken ct)
    {
        var sent = MessagePayload.Read<MarketOfferPayload>(payload);
        var listing = await db.MarketListings.AsNoTracking().FirstOrDefaultAsync(l => l.Id == sent.ListingId, ct)
            ?? throw new ChatRejectedException("That listing isn't there any more.");
        if (!await db.MarketListingPosts.AnyAsync(p => p.ListingId == listing.Id && p.ConversationId == context.Conversation.Id, ct))
            throw new ChatRejectedException("Make an offer in a chat the listing is in.");
        if (listing.SellerId == context.Sender.Id)
            throw new ChatRejectedException("That's your own listing.");
        if (!ListingStatuses.IsOpen(listing.Status))
            throw new ChatRejectedException($"This listing is {ListingStatuses.Label(listing.Status).ToLowerInvariant()}.");
        if (await db.MarketOffers.AnyAsync(o => o.ListingId == listing.Id && o.BuyerId == context.Sender.Id && o.Status == OfferStatuses.Pending, ct))
            throw new ChatRejectedException("You already have an offer waiting on this. Withdraw it to make a new one.");

        var text = ListingMessageHandler.Clean(sent.Text, MarketContract.MaxOfferLength);
        decimal? amount = null;
        string? currency = null;
        switch (sent.Kind)
        {
            case OfferKinds.Cash:
                if (sent.Amount is not ({ } a and > 0 and <= 1_000_000_000))
                    throw new ChatRejectedException("Enter how much you offer.");
                amount = Math.Round(a, 2);
                currency = string.IsNullOrWhiteSpace(sent.Currency) ? listing.Currency : sent.Currency.Trim().ToUpperInvariant();
                if (!MarketContract.Currencies.Contains(currency))
                    throw new ChatRejectedException("Offers can be in kwacha (ZMW) or US dollars (USD).");
                break;
            case OfferKinds.Barter:
                if (text is null)
                    throw new ChatRejectedException("Say what you'd trade, e.g. \"6 crossbreed females\".");
                break;
            default:
                throw new ChatRejectedException("Offer money, or a trade.");
        }

        var offer = new MarketOffer
        {
            Id = Guid.NewGuid(),
            ListingId = listing.Id,
            BuyerId = context.Sender.Id,
            MessageId = context.MessageId,
            ConversationId = context.Conversation.Id,
            Kind = sent.Kind,
            Amount = amount,
            Currency = currency,
            Text = text,
            Status = OfferStatuses.Pending,
            CreatedAt = clock.GetUtcNow()
        };
        db.MarketOffers.Add(offer);
        return new PreparedMessage(
            ContractJson.ToElement(new MarketOfferPayload(offer.Id, listing.Id, listing.Title, listing.SellerId, offer.Kind, amount, currency, text)),
            new OfferState(OfferStatuses.Pending));
    }
}

/// <summary>
/// The Market app (SRS §3.4): the listings in the chats someone is in, their own listings and offers,
/// and the seller's decisions. Every change shows on the listing's or offer's messages too.
/// </summary>
public sealed class MarketService(ChatDbContext db, MessageStateService states, TimeProvider clock)
{
    public const int PageSize = 20;

    /// <summary>Listings shown in chats the user is in (and their own).</summary>
    IQueryable<MarketListing> Visible(Guid userId) =>
        db.MarketListings.Where(l => l.SellerId == userId
            || db.MarketListingPosts.Any(p => p.ListingId == l.Id && db.Members.Any(m => m.ConversationId == p.ConversationId && m.UserId == userId)));

    public async Task<ListingPageDto> FeedAsync(User user, string? species, string? q, string? tag, string? status, DateTimeOffset? before, CancellationToken ct)
    {
        var query = Visible(user.Id);
        query = status switch
        {
            "all" => query,
            { } s when ListingStatuses.All.Contains(s) => query.Where(l => l.Status == s),
            _ => query.Where(l => l.Status == ListingStatuses.Available || l.Status == ListingStatuses.Upcoming)
        };
        if (!string.IsNullOrEmpty(species))
            query = query.Where(l => l.Species == species);
        if (Hashtags.Normalize(tag) is { } t)
            query = query.Where(l => l.Tags.Contains(";" + t + ";"));
        if (q?.Trim() is { Length: > 0 } text)
            query = query.Where(l => l.Title.Contains(text) || (l.Description != null && l.Description.Contains(text))
                || (l.Breed != null && l.Breed.Contains(text)) || (l.Location != null && l.Location.Contains(text)));
        if (before is { } b)
            query = query.Where(l => l.UpdatedAt < b);

        var page = await query.OrderByDescending(l => l.UpdatedAt).Take(PageSize).AsNoTracking().ToListAsync(ct);
        return new ListingPageDto(await ToDtosAsync(user, page, ct), page.Count == PageSize ? page[^1].UpdatedAt : null);
    }

    public async Task<ListingDto?> GetAsync(User user, Guid id, CancellationToken ct)
    {
        var listing = await Visible(user.Id).AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct);
        return listing is null ? null : (await ToDtosAsync(user, [listing], ct))[0];
    }

    public async Task<IReadOnlyList<TagCountDto>> TagsAsync(User user, CancellationToken ct)
    {
        var tags = await Visible(user.Id).Where(l => l.Status == ListingStatuses.Available || l.Status == ListingStatuses.Upcoming)
            .OrderByDescending(l => l.UpdatedAt).Take(500).Select(l => l.Tags).ToListAsync(ct);
        return tags.SelectMany(t => t.Split(';', StringSplitOptions.RemoveEmptyEntries))
            .GroupBy(t => t).Select(g => new TagCountDto(g.Key, g.Count()))
            .OrderByDescending(t => t.Count).ThenBy(t => t.Tag).Take(20).ToList();
    }

    /// <summary>The group chats the user can post a listing in, most active first.</summary>
    public async Task<IReadOnlyList<MarketChatDto>> ChatsAsync(User user, CancellationToken ct) =>
        await db.Conversations.AsNoTracking()
            .Where(c => c.Type == ConversationType.Group && c.Members.Any(m => m.UserId == user.Id))
            .OrderByDescending(c => c.LastActivityAt)
            .Select(c => new MarketChatDto(c.Id, c.Title ?? "Group", c.Members.Count))
            .ToListAsync(ct);

    public async Task<MyMarketDto> MeAsync(User user, CancellationToken ct)
    {
        var selling = await db.MarketListings.AsNoTracking().Where(l => l.SellerId == user.Id).OrderByDescending(l => l.UpdatedAt).Take(100).ToListAsync(ct);
        var made = await db.MarketOffers.AsNoTracking().Where(o => o.BuyerId == user.Id).OrderByDescending(o => o.CreatedAt).Take(100).ToListAsync(ct);
        var received = await db.MarketOffers.AsNoTracking()
            .Where(o => db.MarketListings.Any(l => l.Id == o.ListingId && l.SellerId == user.Id))
            .OrderByDescending(o => o.CreatedAt).Take(100).ToListAsync(ct);
        return new MyMarketDto(await ToDtosAsync(user, selling, ct), await ToDtosAsync(made, ct), await ToDtosAsync(received, ct));
    }

    /// <summary>The offers on a listing: all of them for its seller, otherwise only the viewer's own.</summary>
    public async Task<IReadOnlyList<OfferDto>?> OffersAsync(User user, Guid listingId, CancellationToken ct)
    {
        var listing = await Visible(user.Id).AsNoTracking().FirstOrDefaultAsync(l => l.Id == listingId, ct);
        if (listing is null)
            return null;
        var offers = await db.MarketOffers.AsNoTracking()
            .Where(o => o.ListingId == listingId && (listing.SellerId == user.Id || o.BuyerId == user.Id))
            .OrderByDescending(o => o.CreatedAt).ToListAsync(ct);
        return await ToDtosAsync(offers, ct);
    }

    /// <summary>The seller marks a listing sold, withdrawn, or for sale again; waiting offers on a closed one are declined.</summary>
    public async Task<ListingDto?> SetStatusAsync(User user, Guid id, string status, CancellationToken ct)
    {
        var listing = await db.MarketListings.FirstOrDefaultAsync(l => l.Id == id, ct);
        if (listing is null)
            return null;
        if (listing.SellerId != user.Id)
            throw new ChatRejectedException("Only the seller can change this listing.");
        if (!ListingStatuses.All.Contains(status))
            throw new ChatRejectedException("Choose for sale, coming soon, sold or withdrawn.");

        listing.Status = status;
        listing.UpdatedAt = clock.GetUtcNow();
        var closing = !ListingStatuses.IsOpen(status)
            ? await db.MarketOffers.Where(o => o.ListingId == id && o.Status == OfferStatuses.Pending).ToListAsync(ct)
            : [];
        foreach (var offer in closing)
            (offer.Status, offer.DecidedAt) = (OfferStatuses.Declined, listing.UpdatedAt);
        await db.SaveChangesAsync(ct);

        foreach (var messageId in await db.MarketListingPosts.Where(p => p.ListingId == id).Select(p => p.MessageId).ToListAsync(ct))
            await states.SetAsync(messageId, new ListingState(status), ct);
        foreach (var offer in closing)
            await states.SetAsync(offer.MessageId, new OfferState(offer.Status), ct);
        return await GetAsync(user, id, ct);
    }

    /// <summary>The seller accepts or declines a waiting offer.</summary>
    public async Task<OfferDto?> DecideAsync(User user, Guid offerId, bool accept, CancellationToken ct)
    {
        var offer = await db.MarketOffers.FirstOrDefaultAsync(o => o.Id == offerId, ct);
        if (offer is null)
            return null;
        var listing = await db.MarketListings.AsNoTracking().FirstAsync(l => l.Id == offer.ListingId, ct);
        if (listing.SellerId != user.Id)
            throw new ChatRejectedException("Only the seller can answer this offer.");
        return await MoveAsync(offer, accept ? OfferStatuses.Accepted : OfferStatuses.Declined, ct);
    }

    /// <summary>The buyer takes back a waiting offer.</summary>
    public async Task<OfferDto?> WithdrawAsync(User user, Guid offerId, CancellationToken ct)
    {
        var offer = await db.MarketOffers.FirstOrDefaultAsync(o => o.Id == offerId, ct);
        if (offer is null)
            return null;
        if (offer.BuyerId != user.Id)
            throw new ChatRejectedException("Only the person who made this offer can withdraw it.");
        return await MoveAsync(offer, OfferStatuses.Withdrawn, ct);
    }

    async Task<OfferDto> MoveAsync(MarketOffer offer, string status, CancellationToken ct)
    {
        if (offer.Status != OfferStatuses.Pending)
            throw new ChatRejectedException($"This offer was already {OfferStatuses.Label(offer.Status).ToLowerInvariant()}.");
        (offer.Status, offer.DecidedAt) = (status, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await states.SetAsync(offer.MessageId, new OfferState(status), ct);
        return (await ToDtosAsync([offer], ct))[0];
    }

    // ---- Shapes ----

    internal static string TagField(IReadOnlyList<string> tags) => tags.Count == 0 ? "" : ";" + string.Join(';', tags) + ";";

    static IReadOnlyList<string> TagsOf(MarketListing l) => l.Tags.Split(';', StringSplitOptions.RemoveEmptyEntries);

    static IReadOnlyList<Guid> MediaIdsOf(MarketListing l) =>
        l.MediaIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(id => Guid.ParseExact(id, "N")).ToList();

    internal static async Task<IReadOnlyList<MediaItem>> MediaOfAsync(ChatDbContext db, MarketListing listing, CancellationToken ct)
    {
        var ids = MediaIdsOf(listing);
        var rows = await db.ChatMedia.AsNoTracking().Where(m => ids.Contains(m.Id)).ToListAsync(ct);
        return ids.Select(id => rows.FirstOrDefault(r => r.Id == id)).OfType<ChatMedia>().Select(ChatMediaService.ToItem).ToList();
    }

    internal static MarketListingPayload ToPayload(MarketListing l, IReadOnlyList<MediaItem> media, bool forwarded) =>
        new(l.Id, l.SellerId, l.Title, l.Species, l.Breed, l.Quantity, l.Price, l.Currency, l.Location, l.Age, l.Description,
            l.OpenToBarter, l.BarterTerms, l.Status, media, TagsOf(l), forwarded);

    async Task<IReadOnlyList<ListingDto>> ToDtosAsync(User viewer, IReadOnlyList<MarketListing> listings, CancellationToken ct)
    {
        if (listings.Count == 0)
            return [];
        var ids = listings.Select(l => l.Id).ToList();
        var sellerIds = listings.Select(l => l.SellerId).Distinct().ToList();
        var sellers = await db.Users.AsNoTracking().Where(u => sellerIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);
        var mediaIds = listings.SelectMany(MediaIdsOf).Distinct().ToList();
        var media = await db.ChatMedia.AsNoTracking().Where(m => mediaIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);
        var posts = await db.MarketListingPosts.AsNoTracking()
            .Where(p => ids.Contains(p.ListingId) && db.Members.Any(m => m.ConversationId == p.ConversationId && m.UserId == viewer.Id))
            .OrderByDescending(p => p.PostedAt).ToListAsync(ct);
        var chatIds = posts.Select(p => p.ConversationId).Distinct().ToList();
        var chats = await db.Conversations.AsNoTracking().Where(c => chatIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Title, c.Type, Others = c.Members.Where(m => m.UserId != viewer.Id).Select(m => m.User.DisplayName).ToList() })
            .ToDictionaryAsync(c => c.Id, ct);
        var pending = await db.MarketOffers.AsNoTracking()
            .Where(o => ids.Contains(o.ListingId) && o.Status == OfferStatuses.Pending)
            .GroupBy(o => o.ListingId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);

        return listings.Select(l =>
        {
            var mine = l.SellerId == viewer.Id;
            var inChats = posts.Where(p => p.ListingId == l.Id).DistinctBy(p => p.ConversationId)
                .Select(p => chats.TryGetValue(p.ConversationId, out var c)
                    ? new ListingChatDto(c.Id, c.Title ?? (c.Others.Count > 0 ? string.Join(", ", c.Others) : "Chat"), p.MessageId)
                    : null)
                .OfType<ListingChatDto>().ToList();
            return new ListingDto(
                l.Id,
                ChatMapper.ToDto(sellers[l.SellerId]),
                l.Title, l.Species, l.Breed, l.Quantity, l.Price, l.Currency, l.Location, l.Age, l.Description,
                l.OpenToBarter, l.BarterTerms, l.Status,
                MediaIdsOf(l).Where(media.ContainsKey).Select(id => ChatMediaService.ToItem(media[id])).ToList(),
                TagsOf(l),
                inChats,
                mine,
                mine ? pending.GetValueOrDefault(l.Id) : 0,
                l.CreatedAt,
                l.UpdatedAt);
        }).ToList();
    }

    async Task<IReadOnlyList<OfferDto>> ToDtosAsync(IReadOnlyList<MarketOffer> offers, CancellationToken ct)
    {
        if (offers.Count == 0)
            return [];
        var listingIds = offers.Select(o => o.ListingId).Distinct().ToList();
        var listings = await db.MarketListings.AsNoTracking().Where(l => listingIds.Contains(l.Id))
            .Select(l => new { l.Id, l.Title, l.SellerId }).ToDictionaryAsync(l => l.Id, ct);
        var userIds = offers.Select(o => o.BuyerId).Concat(listings.Values.Select(l => l.SellerId)).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);
        return offers.Select(o =>
        {
            var listing = listings[o.ListingId];
            return new OfferDto(o.Id, o.ListingId, listing.Title, ChatMapper.ToDto(users[o.BuyerId]), ChatMapper.ToDto(users[listing.SellerId]),
                o.Kind, o.Amount, o.Currency, o.Text, o.Status, o.ConversationId, o.CreatedAt);
        }).ToList();
    }
}
