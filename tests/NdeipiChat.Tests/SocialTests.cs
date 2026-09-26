using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using NdeipiChat.Api.Social;
using NdeipiChat.Contracts;
using NdeipiChat.Tests.Infrastructure;
using SkiaSharp;

namespace NdeipiChat.Tests;

public sealed class SocialTests(TestApp app) : IClassFixture<TestApp>
{
    static byte[] Photo(int seed, int width = 1280, int height = 960) => CowPhotos.Face(seed, width, height);

    static MultipartFormDataContent Form(string? caption, bool mint, params byte[][] photos)
    {
        var form = new MultipartFormDataContent();
        if (caption is not null)
            form.Add(new StringContent(caption), "caption");
        form.Add(new StringContent(mint ? "true" : "false"), "mint");
        for (var i = 0; i < photos.Length; i++)
        {
            var part = new ByteArrayContent(photos[i]);
            part.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            form.Add(part, "photos", $"photo{i + 1}.jpg");
        }
        return form;
    }

    static async Task<PostDto> PostAsync(TestUser user, string? caption, bool mint = false, params byte[][] photos)
    {
        using var response = await user.Http.PostAsync("api/posts", Form(caption, mint, photos.Length > 0 ? photos : [Photo(1)]));
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<PostDto>(ContractJson.Options))!;
    }

    static async Task<string> RefusalAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString()!;
    }

    Task<List<Dictionary<string, object?>>> ClaimAsync() =>
        app.SqlAsync("EXEC ndeipi.usp_ClaimTokenTransfers @WorkerId = @w, @BatchSize = 50", new SqlParameter("@w", "ndeipi-1"));

    [Fact]
    public async Task A_post_shows_in_everyones_feed_with_public_photos()
    {
        var alice = await app.CreateUserAsync("Alice Poster");
        var bob = await app.CreateUserAsync("Bob Scroller");

        var post = await PostAsync(alice, "  Sunset over Kariba  ", false, Photo(1, 3000, 2000), Photo(2, 1080, 1350));
        Assert.Equal(("Sunset over Kariba", 2, (PostNftDto?)null), (post.Caption, post.Media.Count, post.Nft));
        Assert.Equal((3000, 2000), (post.Media[0].Width, post.Media[0].Height));

        var feed = await bob.GetAsync<FeedPageDto>("api/posts");
        var seen = feed.Posts.First(p => p.Id == post.Id);
        Assert.Equal(("Alice Poster", 0, false), (seen.Author.DisplayName, seen.LikeCount, seen.LikedByMe));
        Assert.True(feed.MintingEnabled);

        // Photos are public (an NFT's image must load anywhere), immutable, and resized.
        using var anonymous = app.CreateClient();
        foreach (var (size, maxEdge) in PhotoSizes.MaxEdge)
        {
            using var photo = await anonymous.GetAsync(post.Media[0].Url.Replace("/feed", "/" + size));
            Assert.Equal(HttpStatusCode.OK, photo.StatusCode);
            Assert.Equal("image/jpeg", photo.Content.Headers.ContentType?.MediaType);
            Assert.Contains("immutable", photo.Headers.CacheControl?.ToString());
            using var bitmap = SKBitmap.Decode(await photo.Content.ReadAsByteArrayAsync());
            Assert.Equal(Math.Min(maxEdge, 3000), Math.Max(bitmap.Width, bitmap.Height));
        }
    }

    [Fact]
    public async Task Likes_count_once_per_person_and_can_be_taken_back()
    {
        var alice = await app.CreateUserAsync("Alice Liked");
        var bob = await app.CreateUserAsync("Bob Liker");
        var post = await PostAsync(alice, null);

        await bob.PostAsync<LikeResultDto>($"api/posts/{post.Id}/like", new { });
        var twice = await bob.PostAsync<LikeResultDto>($"api/posts/{post.Id}/like", new { });
        Assert.Equal((1, true), (twice.LikeCount, twice.LikedByMe));
        Assert.Equal((1, false), await ViewAsync(alice));

        using var unlike = await bob.Http.DeleteAsync($"api/posts/{post.Id}/like");
        var undone = (await unlike.Content.ReadFromJsonAsync<LikeResultDto>(ContractJson.Options))!;
        Assert.Equal((0, false), (undone.LikeCount, undone.LikedByMe));

        async Task<(int, bool)> ViewAsync(TestUser viewer)
        {
            var seen = await viewer.GetAsync<PostDto>($"api/posts/{post.Id}");
            return (seen.LikeCount, seen.LikedByMe);
        }
    }

    [Fact]
    public async Task Minting_queues_an_NFT_for_Ndeipi_and_the_author_hears_it_land()
    {
        var alice = await app.CreateUserAsync("Alice Minter");
        await alice.Http.PutAsJsonAsync("api/me/wallets", new UserWalletDto("ndeipi", "0xA11CE"), ContractJson.Options);
        await using var aliceHub = await app.ConnectAsync(alice);

        var post = await PostAsync(alice, "First mint", mint: true);
        var nft = post.Nft!;
        Assert.Equal((TransferStatuses.Pending, "ndeipi", TokenStandards.Erc721, TestApp.NftContract), (nft.Status, nft.Chain, nft.Standard, nft.ContractAddress));
        Assert.Equal(PostService.TokenIdFor(post.Id), nft.TokenId);
        Assert.EndsWith($"/nft/posts/{post.Id:N}", nft.MetadataUrl);

        // What Ndeipi Enterprise Server claims: a Mint to Alice, with the tokenURI and no chat.
        var processing = Wait.ForEventAsync<PostNftDto>(aliceHub, nameof(IChatClient.PostNftChanged), n => n.PostId == post.Id && n.Status == TransferStatuses.Processing);
        var row = (await ClaimAsync()).Single(r => (Guid?)r["PostId"] == post.Id);
        Assert.Equal(("Mint", "erc721", "NDPOST", nft.TokenId, 1m), (row["Operation"], row["TokenStandard"], row["TokenSymbol"], row["TokenId"], row["Amount"]));
        Assert.Equal((alice.Id, "0xA11CE", nft.MetadataUrl), (row["RecipientUserId"], row["RecipientWalletAddress"], row["MetadataUri"]));
        Assert.Equal(((object?)null, (object?)null), (row["ConversationId"], row["MessageId"]));
        await processing;

        // The tokenURI resolves, publicly, to ERC-721 metadata pointing at the full-size photo.
        using var anonymous = app.CreateClient();
        var metadata = await anonymous.GetFromJsonAsync<JsonElement>(nft.MetadataUrl);
        Assert.Equal("First mint", metadata.GetProperty("description").GetString());
        Assert.StartsWith("Ndeipi Posts #", metadata.GetProperty("name").GetString());
        Assert.EndsWith("/full", metadata.GetProperty("image").GetString());
        using (var image = await anonymous.GetAsync(metadata.GetProperty("image").GetString()))
            Assert.Equal(HttpStatusCode.OK, image.StatusCode);

        var confirmed = Wait.ForEventAsync<PostNftDto>(aliceHub, nameof(IChatClient.PostNftChanged), n => n.PostId == post.Id && n.Status == TransferStatuses.Confirmed);
        await app.SqlAsync("EXEC ndeipi.usp_CompleteTokenTransfer @Id = @id, @WorkerId = @w, @TxHash = @tx",
            new SqlParameter("@id", row["Id"]), new SqlParameter("@w", "ndeipi-1"), new SqlParameter("@tx", "0xm1n7"));
        Assert.Equal("0xm1n7", (await confirmed).TxHash);
        Assert.Equal(TransferStatuses.Confirmed, (await alice.GetAsync<PostDto>($"api/posts/{post.Id}")).Nft!.Status);

        // Minted posts stay: the NFT shows their photos. Minting again changes nothing.
        using var delete = await alice.Http.DeleteAsync($"api/posts/{post.Id}");
        Assert.Contains("can't be deleted", await RefusalAsync(delete));
        Assert.Equal(nft.TokenId, (await alice.PostAsync<PostDto>($"api/posts/{post.Id}/mint", new { })).Nft!.TokenId);
        Assert.DoesNotContain((await ClaimAsync()), r => (Guid?)r["PostId"] == post.Id);
    }

    [Fact]
    public async Task A_failed_mint_can_be_retried_with_the_same_token_id()
    {
        var alice = await app.CreateUserAsync("Alice Retry");
        var bob = await app.CreateUserAsync("Bob Not Mine");
        var post = await PostAsync(alice, null);

        using (var notMine = await bob.Http.PostAsJsonAsync($"api/posts/{post.Id}/mint", new { }))
            Assert.Contains("Only the person who posted it", await RefusalAsync(notMine));

        var first = (await alice.PostAsync<PostDto>($"api/posts/{post.Id}/mint", new { })).Nft!;
        var row = (await ClaimAsync()).Single(r => (Guid?)r["PostId"] == post.Id);
        await app.SqlAsync("EXEC ndeipi.usp_FailTokenTransfer @Id = @id, @WorkerId = @w, @Error = @e",
            new SqlParameter("@id", row["Id"]), new SqlParameter("@w", "ndeipi-1"), new SqlParameter("@e", "Gas price too high, try later"));
        var failed = (await alice.GetAsync<PostDto>($"api/posts/{post.Id}")).Nft!;
        Assert.Equal((TransferStatuses.Failed, "Gas price too high, try later"), (failed.Status, failed.Error));

        var retry = (await alice.PostAsync<PostDto>($"api/posts/{post.Id}/mint", new { })).Nft!;
        Assert.Equal((TransferStatuses.Pending, first.TokenId), (retry.Status, retry.TokenId));
        var again = (await ClaimAsync()).Single(r => (Guid?)r["PostId"] == post.Id);
        Assert.NotEqual(row["Id"], again["Id"]);
    }

    [Fact]
    public async Task Deleting_an_unminted_post_removes_its_photos()
    {
        var alice = await app.CreateUserAsync("Alice Deleter");
        var bob = await app.CreateUserAsync("Bob Bystander");
        var post = await PostAsync(alice, "oops");

        using (var notYours = await bob.Http.DeleteAsync($"api/posts/{post.Id}"))
            Assert.Contains("your own posts", await RefusalAsync(notYours));

        using (var deleted = await alice.Http.DeleteAsync($"api/posts/{post.Id}"))
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var anonymous = app.CreateClient();
        using var photo = await anonymous.GetAsync(post.Media[0].Url);
        Assert.Equal(HttpStatusCode.NotFound, photo.StatusCode);
        using var gone = await alice.Http.GetAsync($"api/posts/{post.Id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Metadata_exists_only_for_minted_posts()
    {
        var alice = await app.CreateUserAsync("Alice Unminted");
        var post = await PostAsync(alice, null);
        using var anonymous = app.CreateClient();

        using var unminted = await anonymous.GetAsync(SocialContract.MetadataPath(post.Id));
        Assert.Equal(HttpStatusCode.NotFound, unminted.StatusCode);
        using var unknownPhoto = await anonymous.GetAsync(SocialContract.MediaPath(Guid.NewGuid(), PhotoSizes.Feed));
        Assert.Equal(HttpStatusCode.NotFound, unknownPhoto.StatusCode);
        using var badSize = await anonymous.GetAsync(post.Media[0].Url.Replace("/feed", "/huge"));
        Assert.Equal(HttpStatusCode.NotFound, badSize.StatusCode);
    }

    [Fact]
    public async Task The_feed_pages_newest_first()
    {
        var author = await app.CreateUserAsync("Paging Author");
        var created = new List<Guid>();
        for (var i = 0; i < PostService.PageSize + 2; i++)
            created.Add((await PostAsync(author, $"post {i}", false, Photo(100 + i, 400, 300))).Id);

        var first = await author.GetAsync<FeedPageDto>($"api/posts?author={author.Id}");
        Assert.True(first.HasMore);
        Assert.Equal(created.AsEnumerable().Reverse().Take(PostService.PageSize), first.Posts.Select(p => p.Id));

        var second = await author.GetAsync<FeedPageDto>($"api/posts?author={author.Id}&before={first.Posts[^1].Id}");
        Assert.False(second.HasMore);
        Assert.Equal(created.Take(2).Reverse(), second.Posts.Select(p => p.Id));
    }

    [Fact]
    public async Task Bad_posts_are_refused_with_a_reason()
    {
        var alice = await app.CreateUserAsync("Alice Careless");

        async Task<string> TryAsync(string? caption, params byte[][] photos)
        {
            using var response = await alice.Http.PostAsync("api/posts", Form(caption, false, photos));
            return await RefusalAsync(response);
        }

        Assert.Equal("Add at least one photo.", await TryAsync("words only"));
        Assert.Equal("A post can have up to 4 photos.", await TryAsync(null, Photo(1), Photo(2), Photo(3), Photo(4), Photo(5)));
        Assert.Contains("isn't a photo we can read", await TryAsync(null, "not an image"u8.ToArray()));
        Assert.Contains("too small", await TryAsync(null, Photo(1, 150, 150)));
        Assert.Contains("2200 characters", await TryAsync(new string('x', 2201), Photo(1)));
    }
}
