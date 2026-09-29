using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using NdeipiChat.Contracts;
using NdeipiChat.Tests.Infrastructure;

namespace NdeipiChat.Tests;

/// <summary>Comments, reposts, and sorting and filtering the feed.</summary>
public sealed class PostInteractionTests(TestApp app) : IClassFixture<TestApp>
{
    const string Posts = "api/posts";

    static async Task<PostDto> PostAsync(TestUser user, string caption, bool photo = false, Guid? groupId = null)
    {
        var form = new MultipartFormDataContent { { new StringContent(caption), "caption" } };
        if (photo)
        {
            var part = new ByteArrayContent(CowPhotos.Face(caption.Length, 800, 600));
            part.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            form.Add(part, "photos", "photo.jpg");
        }
        if (groupId is { } g)
            form.Add(new StringContent(g.ToString()), "groupId");
        using var response = await user.Http.PostAsync(Posts, form);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<PostDto>(ContractJson.Options))!;
    }

    static async Task<T> SendAsync<T>(TestUser user, HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body, options: ContractJson.Options) };
        using var response = await user.Http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(ContractJson.Options))!;
    }

    [Fact]
    public async Task Comments_are_added_listed_counted_and_deleted_by_their_writer_or_the_posts_author()
    {
        var ivy = await app.CreateUserAsync("Ivy Poster");
        var jon = await app.CreateUserAsync("Jon Commenter");
        var kim = await app.CreateUserAsync("Kim Bystander");
        var post = await PostAsync(ivy, "What do you all think?");

        var first = await SendAsync<CommentDto>(jon, HttpMethod.Post, $"{Posts}/{post.Id}/comments", new AddCommentRequest("  Love it  "));
        await SendAsync<CommentDto>(kim, HttpMethod.Post, $"{Posts}/{post.Id}/comments", new AddCommentRequest("Me too"));
        Assert.Equal(("Love it", true), (first.Text, first.CanDelete));

        var seenByKim = await kim.GetAsync<List<CommentDto>>($"{Posts}/{post.Id}/comments");
        Assert.Equal(["Love it", "Me too"], seenByKim.Select(c => c.Text));
        Assert.Equal([false, true], seenByKim.Select(c => c.CanDelete));
        Assert.Equal(2, (await kim.GetAsync<PostDto>($"{Posts}/{post.Id}")).CommentCount);

        // Kim can't delete Jon's; Ivy can, it's her post.
        using (var notKims = await kim.Http.DeleteAsync($"{Posts}/{post.Id}/comments/{first.Id}"))
            Assert.Equal(HttpStatusCode.BadRequest, notKims.StatusCode);
        using (var ivys = await ivy.Http.DeleteAsync($"{Posts}/{post.Id}/comments/{first.Id}"))
            Assert.Equal(HttpStatusCode.NoContent, ivys.StatusCode);
        Assert.Equal(1, (await ivy.GetAsync<PostDto>($"{Posts}/{post.Id}")).CommentCount);

        using var empty = await jon.Http.PostAsJsonAsync($"{Posts}/{post.Id}/comments", new AddCommentRequest("   "), ContractJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        // Jon's comments show on his profile's Activity, with the post.
        await SendAsync<CommentDto>(jon, HttpMethod.Post, $"{Posts}/{post.Id}/comments", new AddCommentRequest("Second thoughts"));
        var activity = await kim.GetAsync<List<CommentActivityDto>>($"{Posts}/comments?author={jon.Id}");
        Assert.Equal(("Second thoughts", post.Id), (Assert.Single(activity).Comment.Text, activity[0].Post.Id));
    }

    [Fact]
    public async Task A_private_groups_posts_keep_their_comments_inside_and_cant_be_reposted()
    {
        var lea = await app.CreateUserAsync("Lea Owner");
        var max = await app.CreateUserAsync("Max Outsider");
        var group = await SendAsync<GroupDetailDto>(lea, HttpMethod.Post, $"{SocialGraphContract.BasePath}/groups",
            new SaveGroupRequest("Secret Garden", null, null, "🌱", "green", true));
        var post = await PostAsync(lea, "Members only", groupId: group.Group.Id);

        using (var comments = await max.Http.GetAsync($"{Posts}/{post.Id}/comments"))
            Assert.Equal(HttpStatusCode.NotFound, comments.StatusCode);
        using (var comment = await max.Http.PostAsJsonAsync($"{Posts}/{post.Id}/comments", new AddCommentRequest("Let me in"), ContractJson.Options))
            Assert.Equal(HttpStatusCode.NotFound, comment.StatusCode);
        using (var repost = await lea.Http.PostAsJsonAsync($"{Posts}/{post.Id}/repost", new RepostRequest(null), ContractJson.Options))
            Assert.Equal(HttpStatusCode.BadRequest, repost.StatusCode);
    }

    [Fact]
    public async Task Reposts_share_the_original_once_per_person_and_go_when_it_does()
    {
        var nia = await app.CreateUserAsync("Nia Original");
        var oli = await app.CreateUserAsync("Oli Reposter");
        var pat = await app.CreateUserAsync("Pat Second");
        var original = await PostAsync(nia, "Worth sharing", photo: true);

        var repost = await SendAsync<PostDto>(oli, HttpMethod.Post, $"{Posts}/{original.Id}/repost", new RepostRequest("Read this"));
        Assert.Equal(("Read this", original.Id, true), (repost.Caption, repost.RepostOf!.Id, repost.IsRepost));
        Assert.Empty(repost.Media);

        // Reposting again only changes the thoughts; reposting a repost shares the original.
        var again = await SendAsync<PostDto>(oli, HttpMethod.Post, $"{Posts}/{original.Id}/repost", new RepostRequest(null));
        Assert.Equal((repost.Id, (string?)null), (again.Id, again.Caption));
        var viaRepost = await SendAsync<PostDto>(pat, HttpMethod.Post, $"{Posts}/{repost.Id}/repost", new RepostRequest(null));
        Assert.Equal(original.Id, viaRepost.RepostOf!.Id);

        var seen = await oli.GetAsync<PostDto>($"{Posts}/{original.Id}");
        Assert.Equal((2, true), (seen.RepostCount, seen.RepostedByMe));
        Assert.Contains((await pat.GetAsync<FeedPageDto>(Posts)).Posts, p => p.Id == repost.Id && p.RepostOf?.Id == original.Id);

        // Taking it back.
        var undone = await SendAsync<PostDto>(pat, HttpMethod.Delete, $"{Posts}/{original.Id}/repost");
        Assert.Equal((1, false), (undone.RepostCount, undone.RepostedByMe));

        // Deleting the original takes its reposts with it.
        using (var deleted = await nia.Http.DeleteAsync($"{Posts}/{original.Id}"))
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var gone = await oli.Http.GetAsync($"{Posts}/{repost.Id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task The_feed_sorts_by_top_and_a_profile_can_show_only_its_images()
    {
        var quin = await app.CreateUserAsync("Quin Sorter");
        var fans = new List<TestUser>();
        for (var i = 0; i < 3; i++)
            fans.Add(await app.CreateUserAsync($"Fan {i}"));
        var popular = await PostAsync(quin, "Popular one", photo: true);
        var quiet = await PostAsync(quin, "Quiet one, and newer");
        foreach (var fan in fans)
        {
            await SendAsync<LikeResultDto>(fan, HttpMethod.Post, $"{Posts}/{popular.Id}/like");
            await SendAsync<CommentDto>(fan, HttpMethod.Post, $"{Posts}/{popular.Id}/comments", new AddCommentRequest("Nice"));
        }

        var recent = (await quin.GetAsync<FeedPageDto>($"{Posts}?author={quin.Id}")).Posts.Select(p => p.Id).ToList();
        Assert.Equal([quiet.Id, popular.Id], recent);
        var top = (await quin.GetAsync<FeedPageDto>($"{Posts}?author={quin.Id}&sort={FeedSorts.Top}")).Posts.Select(p => p.Id).ToList();
        Assert.Equal([popular.Id, quiet.Id], top);
        Assert.Empty((await quin.GetAsync<FeedPageDto>($"{Posts}?author={quin.Id}&sort={FeedSorts.Top}&skip=2")).Posts);

        var images = (await quin.GetAsync<FeedPageDto>($"{Posts}?author={quin.Id}&images=true")).Posts;
        Assert.Equal(popular.Id, Assert.Single(images).Id);
    }
}
