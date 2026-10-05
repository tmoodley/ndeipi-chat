using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NdeipiChat.Contracts;
using NdeipiChat.Tests.Infrastructure;
using SkiaSharp;

namespace NdeipiChat.Tests;

/// <summary>
/// The livestock marketplace against its SRS (phase 1): photos and videos in chats (§3.3), hashtags
/// and forwarding (§3.2), and sale listings with cash and barter offers in group chats (§3.4),
/// gathered by the Market app.
/// </summary>
public sealed class MarketTests(TestApp app) : IClassFixture<TestApp>
{
    const string Base = MarketContract.BasePath;

    static byte[] Photo(int width = 2400, int height = 1600)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
            canvas.Clear(new SKColor(0x6B, 0x8E, 0x23));
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    /// <summary>The first bytes of an MP4: enough for the server to know it's a video.</summary>
    static byte[] Video() => [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 2, 0, 1, 2, 3, 4];

    static async Task<HttpResponseMessage> UploadRawAsync(TestUser user, Guid chat, byte[] bytes, string name = "photo.png")
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, "file", name);
        return await user.Http.PostAsync($"api/conversations/{chat}/media", form);
    }

    static async Task<MediaItem> UploadAsync(TestUser user, Guid chat, byte[]? bytes = null)
    {
        using var response = await UploadRawAsync(user, chat, bytes ?? Photo());
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<MediaItem>(ContractJson.Options))!;
    }

    static async Task<MessageDto> SendAsync(TestUser user, Guid chat, string kind, object payload) =>
        await user.PostAsync<MessageDto>($"api/conversations/{chat}/messages",
            new SendMessageRequest(chat, kind, ContractJson.ToElement(payload), Guid.NewGuid()));

    static async Task<string> RefusedAsync(TestUser user, Guid chat, string kind, object payload)
    {
        using var response = await user.Http.PostAsJsonAsync($"api/conversations/{chat}/messages",
            new SendMessageRequest(chat, kind, ContractJson.ToElement(payload), Guid.NewGuid()), ContractJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString()!;
    }

    async Task<(TestUser A, TestUser B, ConversationDto Group)> GroupAsync(string name)
    {
        var a = await app.CreateUserAsync($"{name} Seller");
        var b = await app.CreateUserAsync($"{name} Buyer");
        var c = await app.CreateUserAsync($"{name} Member");
        var group = await a.PostAsync<ConversationDto>("api/conversations", new CreateConversationRequest(ConversationType.Group, [b.Id, c.Id], name));
        return (a, b, group);
    }

    static MarketListingPayload Goats(params MediaItem[] media) => new(Guid.Empty, Guid.Empty,
        "Boer goat bucks #QualityGenetics", "goats", "Boer", 3, 12000m, "ZMW", "Monze", "5 years old",
        "Strong bucks from our #QualityGenetics line.", true, "6 better crossbreed females", ListingStatuses.Available, media, []);

    [Fact]
    public async Task Photos_are_compressed_and_sent_together_as_a_gallery_with_a_caption()
    {
        var (a, b, group) = await GroupAsync("Hooves & Feathers");
        var big = await UploadAsync(a, group.Id, Photo(4000, 3000));
        Assert.Equal((MediaTypes.Image, 1600, 1200), (big.Type, big.Width, big.Height));
        Assert.True(big.Size < Photo(4000, 3000).Length);
        var clip = await UploadAsync(a, group.Id, Video());
        Assert.Equal((MediaTypes.Video, (string?)null), (clip.Type, clip.ThumbUrl));

        var sent = await SendAsync(a, group.Id, MessageKinds.Media, new MediaPayload([new(big.Id, "", "", null, null, null, 0), clip], "Goats in the pen #PurePekinPoultry"));
        var payload = ContractJson.Read<MediaPayload>(sent.Payload)!;
        Assert.Equal([big.Id, clip.Id], payload.Items.Select(i => i.Id));
        Assert.Equal(big.Url, payload.Items[0].Url);

        // Seen by the group; served without sign-in, as a small JPEG for the gallery.
        var seen = await b.GetAsync<List<MessageDto>>($"api/conversations/{group.Id}/messages");
        Assert.Equal(MessageKinds.Media, seen[^1].Kind);
        using var thumb = await app.CreateClient().GetAsync(big.ThumbUrl!);
        Assert.Equal("image/jpeg", thumb.Content.Headers.ContentType?.MediaType);
        using var video = await app.CreateClient().GetAsync(clip.Url);
        Assert.Equal("video/mp4", video.Content.Headers.ContentType?.MediaType);

        // An upload goes with one message, in its own chat.
        Assert.Contains("can't be sent here", await RefusedAsync(a, group.Id, MessageKinds.Media, new MediaPayload([big])));
        var outsider = await app.CreateUserAsync("Not In The Group");
        using (var refused = await UploadRawAsync(outsider, group.Id, Photo()))
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        using (var junk = await UploadRawAsync(a, group.Id, Encoding.UTF8.GetBytes("not a photo at all")))
            Assert.Equal(HttpStatusCode.BadRequest, junk.StatusCode);
    }

    [Fact]
    public async Task Messages_forward_to_other_chats_once_marked_forwarded()
    {
        var (a, b, group) = await GroupAsync("Forwarders");
        var other = await a.PostAsync<ConversationDto>("api/conversations", new CreateConversationRequest(ConversationType.Direct, [b.Id], null));
        var photo = await UploadAsync(a, group.Id);
        var gallery = await SendAsync(a, group.Id, MessageKinds.Media, new MediaPayload([photo], "Pekin ducklings"));
        var text = await SendAsync(b, group.Id, MessageKinds.Text, new TextPayload("Ready this Friday"));

        // B forwards A's gallery (B can: they're in the chat it's in).
        var forwarded = await b.PostAsync<List<MessageDto>>($"api/messages/{gallery.Id}/forward", new ForwardRequest([other.Id]));
        var copy = ContractJson.Read<MediaPayload>(Assert.Single(forwarded).Payload)!;
        Assert.Equal((true, photo.Id, "Pekin ducklings"), (copy.Forwarded, copy.Items[0].Id, copy.Caption));

        var textCopy = Assert.Single(await b.PostAsync<List<MessageDto>>($"api/messages/{text.Id}/forward", new ForwardRequest([other.Id])));
        Assert.True(ContractJson.Read<TextPayload>(textCopy.Payload)!.Forwarded);

        // A retry doesn't post it twice.
        var again = Assert.Single(await b.PostAsync<List<MessageDto>>($"api/messages/{text.Id}/forward", new ForwardRequest([other.Id])));
        Assert.Equal(textCopy.Id, again.Id);

        // Someone outside the chat can't forward from it.
        var outsider = await app.CreateUserAsync("Outsider Forwarder");
        using var refused = await outsider.Http.PostAsJsonAsync($"api/messages/{text.Id}/forward", new ForwardRequest([other.Id]), ContractJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task A_listing_posted_in_a_group_shows_in_its_members_Market_with_hashtags_and_follows_forwards()
    {
        var (seller, buyer, group) = await GroupAsync("Goat Traders");
        var photo = await UploadAsync(seller, group.Id);
        var message = await SendAsync(seller, group.Id, MessageKinds.MarketListing, Goats(photo));
        var listing = ContractJson.Read<MarketListingPayload>(message.Payload)!;
        Assert.NotEqual(Guid.Empty, listing.ListingId);
        Assert.Equal((seller.Id, "ZMW", 12000m, ListingStatuses.Available), (listing.SellerId, listing.Currency, listing.Price, listing.Status));
        Assert.Equal(["qualitygenetics"], listing.Tags);
        Assert.Equal(photo.Id, Assert.Single(listing.Media).Id);
        Assert.Equal("K12,000", MarketContract.Price(listing.Price, listing.Currency));

        var feed = await buyer.GetAsync<ListingPageDto>($"{Base}/listings");
        var seen = Assert.Single(feed.Listings, l => l.Id == listing.ListingId);
        Assert.Equal(("Goat Traders", message.Id, false), (Assert.Single(seen.Chats).Title, seen.Chats[0].MessageId, seen.IsMine));
        Assert.Contains((await buyer.GetAsync<ListingPageDto>($"{Base}/listings?tag=%23QualityGenetics")).Listings, l => l.Id == listing.ListingId);
        Assert.DoesNotContain((await buyer.GetAsync<ListingPageDto>($"{Base}/listings?species=cattle")).Listings, l => l.Id == listing.ListingId);
        Assert.Contains((await buyer.GetAsync<List<TagCountDto>>($"{Base}/tags")), t => t.Tag == "qualitygenetics");

        // Not in the group: not in their Market. Forwarded into their chat: it is, as the same listing.
        var friend = await app.CreateUserAsync("Friend Of Buyer");
        Assert.DoesNotContain((await friend.GetAsync<ListingPageDto>($"{Base}/listings")).Listings, l => l.Id == listing.ListingId);
        var dm = await buyer.PostAsync<ConversationDto>("api/conversations", new CreateConversationRequest(ConversationType.Direct, [friend.Id], null));
        var forwarded = Assert.Single(await buyer.PostAsync<List<MessageDto>>($"api/messages/{message.Id}/forward", new ForwardRequest([dm.Id])));
        Assert.Equal(listing.ListingId, ContractJson.Read<MarketListingPayload>(forwarded.Payload)!.ListingId);
        Assert.Contains((await friend.GetAsync<ListingPageDto>($"{Base}/listings")).Listings, l => l.Id == listing.ListingId);

        // Validation.
        Assert.Contains("title", await RefusedAsync(seller, group.Id, MessageKinds.MarketListing, Goats() with { Title = " " }));
        Assert.Contains("kwacha", await RefusedAsync(seller, group.Id, MessageKinds.MarketListing, Goats() with { Currency = "EUR" }));
    }

    [Fact]
    public async Task Buyers_offer_cash_or_a_trade_and_the_seller_decides_and_selling_closes_the_rest()
    {
        var (seller, buyer, group) = await GroupAsync("Barter Group");
        var message = await SendAsync(seller, group.Id, MessageKinds.MarketListing, Goats());
        var listingId = ContractJson.Read<MarketListingPayload>(message.Payload)!.ListingId;

        Assert.Contains("own listing", await RefusedAsync(seller, group.Id, MessageKinds.MarketOffer, new MarketOfferPayload(Guid.Empty, listingId, "", Guid.Empty, OfferKinds.Cash, 10000m, null, null)));
        Assert.Contains("trade", await RefusedAsync(buyer, group.Id, MessageKinds.MarketOffer, new MarketOfferPayload(Guid.Empty, listingId, "", Guid.Empty, OfferKinds.Barter, null, null, " ")));

        var cash = await SendAsync(buyer, group.Id, MessageKinds.MarketOffer, new MarketOfferPayload(Guid.Empty, listingId, "", Guid.Empty, OfferKinds.Cash, 10500m, null, "Can collect Saturday"));
        var offer = ContractJson.Read<MarketOfferPayload>(cash.Payload)!;
        Assert.Equal((seller.Id, "ZMW", "Boer goat bucks #QualityGenetics"), (offer.SellerId, offer.Currency, offer.ListingTitle));
        Assert.Equal(OfferStatuses.Pending, ContractJson.Read<OfferState>(cash.State!.Value)!.Status);
        Assert.Contains("already have an offer", await RefusedAsync(buyer, group.Id, MessageKinds.MarketOffer, new MarketOfferPayload(Guid.Empty, listingId, "", Guid.Empty, OfferKinds.Cash, 11000m, null, null)));

        // An offer must be made where the listing is.
        var elsewhere = await buyer.PostAsync<ConversationDto>("api/conversations", new CreateConversationRequest(ConversationType.Direct, [seller.Id], null));
        Assert.Contains("chat the listing is in", await RefusedAsync(buyer, elsewhere.Id, MessageKinds.MarketOffer, new MarketOfferPayload(Guid.Empty, listingId, "", Guid.Empty, OfferKinds.Cash, 1m, null, null)));

        // The seller sees it, and only they can answer it.
        var mine = await seller.GetAsync<ListingDto>($"{Base}/listings/{listingId}");
        Assert.Equal(1, mine.OfferCount);
        using (var notTheirs = await buyer.Http.PostAsync($"{Base}/offers/{offer.OfferId}/accept", null))
            Assert.Equal(HttpStatusCode.BadRequest, notTheirs.StatusCode);
        var declined = await seller.PostAsync<OfferDto>($"{Base}/offers/{offer.OfferId}/decline", new { });
        Assert.Equal(OfferStatuses.Declined, declined.Status);
        var updated = (await buyer.GetAsync<List<MessageDto>>($"api/conversations/{group.Id}/messages")).Single(m => m.Id == cash.Id);
        Assert.Equal(OfferStatuses.Declined, ContractJson.Read<OfferState>(updated.State!.Value)!.Status);

        // A barter offer, then the seller marks it sold: the waiting offer is declined and every copy shows "sold".
        var barter = await SendAsync(buyer, group.Id, MessageKinds.MarketOffer, new MarketOfferPayload(Guid.Empty, listingId, "", Guid.Empty, OfferKinds.Barter, null, null, "6 crossbreed females"));
        var sold = await seller.Http.PutAsJsonAsync($"{Base}/listings/{listingId}/status", new SetListingStatusRequest(ListingStatuses.Sold), ContractJson.Options);
        sold.EnsureSuccessStatusCode();
        var after = await buyer.GetAsync<List<MessageDto>>($"api/conversations/{group.Id}/messages");
        Assert.Equal(ListingStatuses.Sold, ContractJson.Read<ListingState>(after.Single(m => m.Id == message.Id).State!.Value)!.Status);
        Assert.Equal(OfferStatuses.Declined, ContractJson.Read<OfferState>(after.Single(m => m.Id == barter.Id).State!.Value)!.Status);
        Assert.Contains("sold", await RefusedAsync(buyer, group.Id, MessageKinds.MarketOffer, new MarketOfferPayload(Guid.Empty, listingId, "", Guid.Empty, OfferKinds.Cash, 1m, null, null)));

        // Sold listings drop out of the feed, but stay on the seller's page.
        Assert.DoesNotContain((await buyer.GetAsync<ListingPageDto>($"{Base}/listings")).Listings, l => l.Id == listingId);
        var me = await seller.GetAsync<MyMarketDto>($"{Base}/me");
        Assert.Contains(me.Selling, l => l.Id == listingId && l.Status == ListingStatuses.Sold);
        Assert.Equal(2, me.OffersReceived.Count(o => o.ListingId == listingId));
        Assert.Contains((await buyer.GetAsync<MyMarketDto>($"{Base}/me")).OffersMade, o => o.Id == offer.OfferId);
    }

    [Fact]
    public void Hashtags_are_found_as_written_and_stored_lower_case() =>
        Assert.Equal(["purepekinpoultry", "qualitygenetics"],
            Hashtags.Find("Our upcoming Pekin parent flock #PurePekinPoultry", "#QualityGenetics #purepekinpoultry, email a#b"));
}
