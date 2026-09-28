using Microsoft.AspNetCore.Components;
using NdeipiChat.Client.Realtime;
using NdeipiChat.Client.ViewModels;
using NdeipiChat.Contracts;

namespace NdeipiChat.Web.Platform;

/// <summary>
/// What Social's cards and profiles can do with a person, joining it up with the other apps:
/// follow them, message them (Chats), send them money or tokens (the chat's exchange panel), add
/// them as a Shamwari, or hire them (Gigs).
/// </summary>
public sealed class SocialActions(ChatApi api, NavigationManager navigation, LauncherViewModel launcher)
{
    public const string Base = SocialGraphContract.BasePath;

    public Task FollowAsync(Guid userId, bool follow) =>
        api.SendJsonAsync<object>(follow ? HttpMethod.Post : HttpMethod.Delete, $"{Base}/follows/{userId}");

    /// <summary>Opens (or starts) your one-to-one chat with them; <paramref name="panel"/> opens the exchange panel on "send-money" or "transfer".</summary>
    public async Task MessageAsync(Guid userId, string? panel = null)
    {
        var chat = await api.CreateConversationAsync(new CreateConversationRequest(ConversationType.Direct, [userId], null));
        navigation.NavigateTo(panel is null ? $"chat/{chat.Id}" : $"chat/{chat.Id}?panel={panel}");
    }

    public Task AddShamwariAsync(Guid userId) => api.SendJsonAsync<AddShamwariResponse>(HttpMethod.Post, $"api/shamwaris/users/{userId}");

    public bool CanHire => launcher.Allows(GigsContract.AppId);

    /// <summary>Opens Gigs on a new gig offered to them.</summary>
    public void Hire(Guid userId) => navigation.NavigateTo($"apps/{GigsContract.AppId}?hire={userId:N}");

    public bool CanChat => launcher.Allows(BuiltInApps.Chats);

    public bool CanPay => launcher.Allows(BuiltInApps.Wallet);

    /// <summary>A banner colour per person or group, stable across visits.</summary>
    public static string ToneFor(Guid id) => AppLook.Tone("u" + id.ToString("N")[..6]);
}
