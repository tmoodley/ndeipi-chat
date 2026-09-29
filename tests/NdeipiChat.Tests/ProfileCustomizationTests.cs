using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using NdeipiChat.Contracts;
using NdeipiChat.Tests.Infrastructure;

namespace NdeipiChat.Tests;

/// <summary>Profile details, profile and group pictures, and managing (and deleting) a group.</summary>
public sealed class ProfileCustomizationTests(TestApp app) : IClassFixture<TestApp>
{
    const string Base = SocialGraphContract.BasePath;

    static MultipartFormDataContent Image(byte[] bytes)
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        return new MultipartFormDataContent { { part, "image", "image.jpg" } };
    }

    static async Task<T> SendAsync<T>(TestUser user, HttpMethod method, string path, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        using var response = await user.Http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(ContractJson.Options))!;
    }

    static ProfileDetailsDto Details(IReadOnlyList<string>? skills = null, IReadOnlyList<SocialLinkDto>? links = null) => new(
        "Photographer", "Zimbabwe", skills ?? ["Photography", "Editing", "photography"],
        [new InterestDto("Music", "Oliver Mtukudzi, Jah Prayzah")],
        [new TimelineEntryDto("Lead photographer", "Studio 263", "2019 - now", "Weddings and events.")],
        [new TimelineEntryDto("BA Fine Art", "University of Zimbabwe", "2014 - 2017", null)],
        links ?? [new SocialLinkDto("instagram", "instagram.com/erin")]);

    [Fact]
    public async Task Profile_details_are_saved_cleaned_up_and_shown_to_others()
    {
        var erin = await app.CreateUserAsync("Erin Details");
        var before = await erin.GetAsync<SocialProfileDto>($"{Base}/profiles/{erin.Id}");

        using (var put = await erin.Http.PutAsJsonAsync($"{Base}/profile/details", Details(), ContractJson.Options))
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var viewer = await app.CreateUserAsync("Details Viewer");
        var seen = await viewer.GetAsync<SocialProfileDto>($"{Base}/profiles/{erin.Id}");
        var d = seen.Details!;
        Assert.Equal(("Photographer", "Zimbabwe"), (d.Occupation, d.Country));
        Assert.Equal(["Photography", "Editing"], d.Skills);
        Assert.Equal("Studio 263", Assert.Single(d.Jobs).Place);
        Assert.Equal("https://instagram.com/erin", Assert.Single(d.Links).Url);
        Assert.True(seen.Completion > before.Completion);

        foreach (var bad in new[]
        {
            Details(skills: Enumerable.Range(0, ProfileLimits.MaxSkills + 1).Select(i => $"skill {i}").ToList()),
            Details(links: [new SocialLinkDto("website", "javascript:alert(1)")]),
            Details(links: [new SocialLinkDto("website", "ftp://files.example")])
        })
        {
            using var response = await erin.Http.PutAsJsonAsync($"{Base}/profile/details", bad, ContractJson.Options);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // A network we don't have an icon for is kept, as a plain link.
        await SendAsync<SocialProfileDto>(erin, HttpMethod.Put, $"{Base}/profile/details",
            JsonContent.Create(Details(links: [new SocialLinkDto("myspace", "myspace.com/erin")]), options: ContractJson.Options));
        Assert.Equal("website", Assert.Single((await viewer.GetAsync<SocialProfileDto>($"{Base}/profiles/{erin.Id}")).Details!.Links).Kind);
    }

    [Fact]
    public async Task A_custom_avatar_and_cover_can_be_set_and_removed()
    {
        var finn = await app.CreateUserAsync("Finn Pictures");
        var original = (await finn.GetAsync<SocialProfileDto>($"{Base}/profiles/{finn.Id}")).User.AvatarUrl;

        var withAvatar = await SendAsync<SocialProfileDto>(finn, HttpMethod.Post, $"{Base}/profile/avatar", Image(CowPhotos.Face(7, 600, 600)));
        Assert.True(withAvatar.HasCustomAvatar);
        Assert.EndsWith("/thumb", withAvatar.User.AvatarUrl);
        using (var anonymous = app.CreateClient())
        using (var photo = await anonymous.GetAsync(withAvatar.User.AvatarUrl))
            Assert.Equal(HttpStatusCode.OK, photo.StatusCode);

        var withCover = await SendAsync<SocialProfileDto>(finn, HttpMethod.Post, $"{Base}/profile/cover", Image(CowPhotos.Face(8, 1600, 600)));
        Assert.EndsWith("/feed", withCover.CoverUrl);

        var reverted = await SendAsync<SocialProfileDto>(finn, HttpMethod.Delete, $"{Base}/profile/avatar");
        Assert.Equal((false, original), (reverted.HasCustomAvatar, reverted.User.AvatarUrl));
        Assert.NotNull(reverted.CoverUrl);
        Assert.Null((await SendAsync<SocialProfileDto>(finn, HttpMethod.Delete, $"{Base}/profile/cover")).CoverUrl);

        using var tiny = await finn.Http.PostAsync($"{Base}/profile/avatar", Image(CowPhotos.Face(9, 60, 60)));
        Assert.Equal(HttpStatusCode.BadRequest, tiny.StatusCode);
    }

    [Fact]
    public async Task An_organizer_manages_their_groups_pictures_and_details_and_can_delete_it()
    {
        var gail = await app.CreateUserAsync("Gail Organizer");
        var hugo = await app.CreateUserAsync("Hugo Member");
        var group = await SendAsync<GroupDetailDto>(gail, HttpMethod.Post, $"{Base}/groups", JsonContent.Create(
            new SaveGroupRequest("Harare Shooters", "Photographers in Harare", null, "📷", "blue", false, "Photo walks every Saturday", "hello@shooters.example", "shooters.example"),
            options: ContractJson.Options));
        var id = group.Group.Id;
        Assert.Equal(("Photo walks every Saturday", "hello@shooters.example", "https://shooters.example"), (group.Group.Tagline, group.Email, group.Website));

        using (var badEmail = await gail.Http.PutAsJsonAsync($"{Base}/groups/{id}",
                   new SaveGroupRequest("Harare Shooters", null, null, "📷", "blue", false, null, "not an email"), ContractJson.Options))
            Assert.Equal(HttpStatusCode.BadRequest, badEmail.StatusCode);

        var withCover = await SendAsync<GroupDetailDto>(gail, HttpMethod.Post, $"{Base}/groups/{id}/cover", Image(CowPhotos.Face(10, 1600, 600)));
        Assert.NotNull(withCover.Group.CoverUrl);
        Assert.NotNull((await SendAsync<GroupDetailDto>(gail, HttpMethod.Post, $"{Base}/groups/{id}/avatar", Image(CowPhotos.Face(11, 500, 500)))).Group.AvatarUrl);

        await SendAsync<GroupDetailDto>(hugo, HttpMethod.Post, $"{Base}/groups/{id}/join");
        using (var notTheirs = await hugo.Http.PostAsync($"{Base}/groups/{id}/avatar", Image(CowPhotos.Face(12, 500, 500))))
            Assert.Equal(HttpStatusCode.NotFound, notTheirs.StatusCode);
        using (var notTheirsEither = await hugo.Http.DeleteAsync($"{Base}/groups/{id}"))
            Assert.Equal(HttpStatusCode.NotFound, notTheirsEither.StatusCode);

        using (var post = new MultipartFormDataContent { { new StringContent("Walk this Saturday"), "caption" }, { new StringContent(id.ToString()), "groupId" } })
        using (var posted = await hugo.Http.PostAsync("api/posts", post))
            Assert.True(posted.IsSuccessStatusCode);

        using (var deleted = await gail.Http.DeleteAsync($"{Base}/groups/{id}"))
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using (var gone = await hugo.Http.GetAsync($"{Base}/groups/{id}"))
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.DoesNotContain((await hugo.GetAsync<FeedPageDto>("api/posts")).Posts, p => p.Group?.Id == id);
    }
}
