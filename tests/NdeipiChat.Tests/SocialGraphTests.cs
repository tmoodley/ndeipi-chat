using System.Net;
using System.Net.Http.Json;
using NdeipiChat.Contracts;
using NdeipiChat.Tests.Infrastructure;

namespace NdeipiChat.Tests;

public sealed class SocialGraphTests(TestApp app) : IClassFixture<TestApp>
{
    const string Base = SocialGraphContract.BasePath;

    static MultipartFormDataContent TextPost(string caption, Guid? groupId = null)
    {
        var form = new MultipartFormDataContent { { new StringContent(caption), "caption" } };
        if (groupId is { } g)
            form.Add(new StringContent(g.ToString()), "groupId");
        return form;
    }

    static async Task<PostDto> PostTextAsync(TestUser user, string caption, Guid? groupId = null)
    {
        using var response = await user.Http.PostAsync("api/posts", TextPost(caption, groupId));
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<PostDto>(ContractJson.Options))!;
    }

    static async Task<T> PutAsync<T>(TestUser user, string path, object body)
    {
        using var response = await user.Http.PutAsJsonAsync(path, body, ContractJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(ContractJson.Options))!;
    }

    [Fact]
    public async Task Following_someone_brings_their_posts_into_your_following_feed_and_shows_on_profiles()
    {
        var ana = await app.CreateUserAsync("Ana Follower");
        var ben = await app.CreateUserAsync("Ben Followed");
        var cal = await app.CreateUserAsync("Cal Stranger");
        var benPost = await PostTextAsync(ben, "Ben's words, no photo needed");
        var calPost = await PostTextAsync(cal, "Cal's words");

        using (var follow = await ana.Http.PostAsync($"{Base}/follows/{ben.Id}", null))
            Assert.Equal(HttpStatusCode.NoContent, follow.StatusCode);
        using (var again = await ana.Http.PostAsync($"{Base}/follows/{ben.Id}", null))
            Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        using (var self = await ana.Http.PostAsync($"{Base}/follows/{ana.Id}", null))
            Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);

        var following = await ana.GetAsync<FeedPageDto>($"api/posts?scope={FeedScopes.Following}");
        Assert.Contains(following.Posts, p => p.Id == benPost.Id);
        Assert.DoesNotContain(following.Posts, p => p.Id == calPost.Id);
        Assert.Contains((await ana.GetAsync<FeedPageDto>("api/posts")).Posts, p => p.Id == calPost.Id);

        var benProfile = await ana.GetAsync<SocialProfileDto>($"{Base}/profiles/{ben.Id}");
        Assert.Equal((true, false, 1, 1), (benProfile.IFollow, benProfile.FollowsMe, benProfile.FollowerCount, benProfile.PostCount));
        Assert.Contains((await ben.GetAsync<List<PersonCardDto>>($"{Base}/profiles/{ben.Id}/followers")), c => c.User.Id == ana.Id);

        using (var unfollow = await ana.Http.DeleteAsync($"{Base}/follows/{ben.Id}"))
            Assert.Equal(HttpStatusCode.NoContent, unfollow.StatusCode);
        Assert.False((await ana.GetAsync<SocialProfileDto>($"{Base}/profiles/{ben.Id}")).IFollow);
    }

    [Fact]
    public async Task A_profile_carries_a_bio_and_the_persons_gig_work()
    {
        var dana = await app.CreateUserAsync("Dana Profile");
        var saved = await PutAsync<SocialProfileDto>(dana, $"{Base}/profile", new SaveSocialProfileRequest("I build things.", "Harare", "dana.example"));
        Assert.Equal(("I build things.", "Harare", "https://dana.example"), (saved.Bio, saved.City, saved.Website));
        Assert.Null(saved.Gig);

        await PutAsync<GigProfileDto>(dana, "api/gigs/profile", new SaveGigProfileRequest("Builder", ["construction"], null, -17.8, 31.0));
        var viewer = await app.CreateUserAsync("Profile Viewer");
        var seen = await viewer.GetAsync<SocialProfileDto>($"{Base}/profiles/{dana.Id}");
        Assert.Equal(("Builder", false), (seen.Gig!.Headline, seen.IsMe));

        using var bad = await dana.Http.PutAsJsonAsync($"{Base}/profile", new SaveSocialProfileRequest(new string('x', 501), null, null), ContractJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Public_groups_are_open_and_private_groups_keep_their_posts_to_members()
    {
        var owner = await app.CreateUserAsync("Group Owner", $"{Guid.NewGuid():N}@example.test");
        var member = await app.CreateUserAsync("Group Member", $"{Guid.NewGuid():N}@example.test");
        var outsider = await app.CreateUserAsync("Group Outsider");

        var open = await owner.PostAsync<GroupDetailDto>($"{Base}/groups", new SaveGroupRequest("Harare Photographers", "Share your shots", "Be kind", "📷", "pink", false));
        Assert.Equal((1, GroupRoles.Owner), (open.Group.MemberCount, open.Group.MyRole));

        // Anyone can join a public group and post in it; outsiders can read it.
        var joined = await member.PostAsync<GroupDetailDto>($"{Base}/groups/{open.Group.Id}/join", new { });
        Assert.Equal((2, true), (joined.Group.MemberCount, joined.Group.IsMember));
        var post = await PostTextAsync(member, "Golden hour at Domboshava", open.Group.Id);
        Assert.Equal(open.Group.Id, post.Group!.Id);
        Assert.Contains((await outsider.GetAsync<FeedPageDto>($"api/posts?group={open.Group.Id}")).Posts, p => p.Id == post.Id);

        // Not a member: can't post in it.
        using (var refused = await outsider.Http.PostAsync("api/posts", TextPost("Let me in", open.Group.Id)))
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        // A private group: joining needs the owner; its posts stay inside.
        var closed = await owner.PostAsync<GroupDetailDto>($"{Base}/groups", new SaveGroupRequest("Committee", null, "Keep it confidential", null, null, true));
        using (var knock = await outsider.Http.PostAsync($"{Base}/groups/{closed.Group.Id}/join", null))
            Assert.Equal(HttpStatusCode.BadRequest, knock.StatusCode);
        var memberEmail = await app.DbAsync(db => Task.FromResult(db.Users.Single(u => u.Id == member.Id).Email!));
        await owner.PostAsync<PersonCardDto>($"{Base}/groups/{closed.Group.Id}/members", new AddGroupMemberRequest(memberEmail));
        var secret = await PostTextAsync(member, "Minutes of the meeting", closed.Group.Id);

        Assert.DoesNotContain((await outsider.GetAsync<FeedPageDto>("api/posts")).Posts, p => p.Id == secret.Id);
        using (var hidden = await outsider.Http.GetAsync($"api/posts/{secret.Id}"))
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        var outsiderView = await outsider.GetAsync<GroupDetailDto>($"{Base}/groups/{closed.Group.Id}");
        Assert.Equal(("", 0), (outsiderView.Rules, outsiderView.RecentMembers.Count));
        Assert.DoesNotContain(await outsider.GetAsync<List<GroupSummaryDto>>($"{Base}/groups"), g => g.Id == closed.Group.Id);

        // The member's following feed includes their groups.
        Assert.Contains((await member.GetAsync<FeedPageDto>($"api/posts?scope={FeedScopes.Following}")).Posts, p => p.Id == secret.Id);
        Assert.Equal(2, (await member.GetAsync<List<GroupSummaryDto>>($"{Base}/groups?mine=true")).Count);

        // The owner can't walk out on their group; a member can.
        using (var ownerLeaves = await owner.Http.PostAsync($"{Base}/groups/{open.Group.Id}/leave", null))
            Assert.Equal(HttpStatusCode.BadRequest, ownerLeaves.StatusCode);
        Assert.False((await member.PostAsync<GroupDetailDto>($"{Base}/groups/{open.Group.Id}/leave", new { })).Group.IsMember);
    }

    [Fact]
    public async Task The_gig_board_lists_open_gigs_by_category_and_a_skilled_worker_can_take_one()
    {
        var client = await app.CreateUserAsync("Board Client");
        var worker = await app.CreateUserAsync("Board Worker");
        var plumber = await app.CreateUserAsync("Board Plumber");
        var gig = await client.PostAsync<GigDto>("api/gigs", new CreateGigRequest("Tutor my son", null, "tutoring", 10.5, 10.5, "Test", "12"));

        var categories = await worker.GetAsync<List<GigCategoryDto>>("api/gigs/board/categories");
        Assert.True(categories.Single(c => c.Skill == "tutoring").OpenCount >= 1);
        Assert.Equal(GigSkills.All.Count, categories.Count);
        Assert.Contains(await worker.GetAsync<List<GigDto>>("api/gigs/board?skill=tutoring"), g => g.Id == gig.Id);
        Assert.DoesNotContain(await worker.GetAsync<List<GigDto>>("api/gigs/board?skill=delivery"), g => g.Id == gig.Id);

        // No work profile, or the wrong skills: can't take it.
        using (var noProfile = await worker.Http.PostAsync($"api/gigs/{gig.Id}/accept", null))
            Assert.Contains("work profile", await noProfile.Content.ReadAsStringAsync());
        await PutAsync<GigProfileDto>(plumber, "api/gigs/profile", new SaveGigProfileRequest("Plumber", ["construction"], null, 10.5, 10.6));
        using (var wrongSkill = await plumber.Http.PostAsync($"api/gigs/{gig.Id}/accept", null))
            Assert.Equal(HttpStatusCode.BadRequest, wrongSkill.StatusCode);

        await PutAsync<GigProfileDto>(worker, "api/gigs/profile", new SaveGigProfileRequest("Maths tutor", ["tutoring"], null, 10.5, 10.6));
        var taken = await worker.PostAsync<GigDto>($"api/gigs/{gig.Id}/accept", new { });
        Assert.Equal((GigStatuses.Assigned, true), (taken.Status, taken.IsWorker));
        Assert.DoesNotContain(await client.GetAsync<List<GigDto>>("api/gigs/board?skill=tutoring"), g => g.Id == gig.Id);
    }
}
