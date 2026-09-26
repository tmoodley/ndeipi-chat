using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.SqlClient;
using NdeipiChat.Client;
using NdeipiChat.Client.Auth;
using NdeipiChat.Client.Extensions;
using NdeipiChat.Client.Realtime;
using NdeipiChat.Client.ViewModels;
using NdeipiChat.Contracts;
using NdeipiChat.Tests.Infrastructure;

namespace NdeipiChat.Tests;

/// <summary>The app's own code -- sign-in, connection, view models -- against the real API.</summary>
public sealed class ClientTests(TestApp app) : IClassFixture<TestApp>
{
    static Dictionary<string, object> Open(Guid conversationId) => new() { [Routes.ConversationIdParameter] = conversationId };

    [Fact]
    public async Task Two_app_users_chat_through_the_view_models()
    {
        var alice = await app.CreateUserAsync("Alice Chirwa");
        var bob = await app.CreateUserAsync("Bob Sibanda");
        await using var aliceApp = await ClientHarness.SignInAsync(app, alice);
        await using var bobApp = await ClientHarness.SignInAsync(app, bob);
        var chat = await aliceApp.Api.CreateConversationAsync(new CreateConversationRequest(ConversationType.Direct, [bob.Id], null));

        var bobChats = bobApp.Chats();
        await bobChats.RefreshCommand.ExecuteAsync(null);
        var bobChat = bobApp.Chat();
        await bobChat.OnNavigatedToAsync(Open(chat.Id));
        var aliceChat = aliceApp.Chat();
        await aliceChat.OnNavigatedToAsync(Open(chat.Id));
        aliceChat.Activate();
        Assert.Equal(["Transfer", "Send money"], aliceChat.Actions.Select(a => a.Title));

        aliceChat.Draft = "Makadii?";
        await aliceChat.SendTextCommand.ExecuteAsync(null);

        var mine = Assert.IsType<TextMessageViewModel>(aliceApp.Ui.Read(() => Assert.Single(aliceChat.Messages)));
        Assert.Equal((DeliveryStatus.Sent, true, ""), (mine.Delivery, mine.IsMine, aliceChat.Draft));

        await Wait.UntilAsync(() => bobApp.Ui.Read(() => bobChat.Messages.Count == 1), "Bob's open chat shows the message");
        var theirs = Assert.IsType<TextMessageViewModel>(bobApp.Ui.Read(() => bobChat.Messages[0]));
        Assert.Equal(("Makadii?", false, "Alice Chirwa"), (theirs.Text, theirs.IsMine, theirs.SenderName));

        await Wait.UntilAsync(() => bobApp.Ui.Read(() => bobChats.Conversations.FirstOrDefault(c => c.Id == chat.Id)?.Preview == "Makadii?"), "Bob's chat list shows the preview");
        Assert.Equal(1, bobApp.Ui.Read(() => bobChats.TotalUnread));
    }

    [Fact]
    public async Task A_token_transfer_bubble_follows_Ndeipi_to_confirmed()
    {
        var alice = await app.CreateUserAsync("Alice Bubble");
        var bob = await app.CreateUserAsync("Bob Bubble");
        await using var aliceApp = await ClientHarness.SignInAsync(app, alice);
        await using var bobApp = await ClientHarness.SignInAsync(app, bob);
        var chat = await aliceApp.Api.CreateConversationAsync(new CreateConversationRequest(ConversationType.Direct, [bob.Id], null));

        var bobChat = bobApp.Chat();
        await bobChat.OnNavigatedToAsync(Open(chat.Id));

        var form = aliceApp.AssetTransfer();
        await form.OnNavigatedToAsync(Open(chat.Id));
        Assert.Equal(bob.Id, form.Recipient?.Id);
        Assert.False(form.ShowRecipientPicker);
        Assert.Equal("NMX", form.SelectedToken?.Token?.Symbol);

        form.Amount = "7,25";
        form.Memo = "for the tickets";
        await form.SendCommand.ExecuteAsync(null);
        Assert.Null(form.ErrorMessage);
        Assert.True(aliceApp.Navigator.WentBack);

        await Wait.UntilAsync(() => bobApp.Ui.Read(() => bobChat.Messages.OfType<AssetTransferMessageViewModel>().Any()), "Bob sees the transfer");
        var bubble = bobApp.Ui.Read(() => bobChat.Messages.OfType<AssetTransferMessageViewModel>().Single());
        Assert.Equal(("7.25 NMX", "From Alice Bubble", "Queued"), (bubble.AmountText, bubble.Headline, bubble.StatusText));

        var transferId = (Guid)(await app.SqlAsync("SELECT Id FROM ndeipi.TokenTransferQueue WHERE MessageId = @m", new SqlParameter("@m", bubble.Id))).Single()["Id"]!;
        await app.SqlAsync("EXEC ndeipi.usp_ClaimTokenTransfers @WorkerId = 'ndeipi-client', @BatchSize = 100");
        await app.SqlAsync("EXEC ndeipi.usp_CompleteTokenTransfer @Id = @id, @WorkerId = 'ndeipi-client', @TxHash = '0xbeef'", new SqlParameter("@id", transferId));

        await Wait.UntilAsync(() => bubble.IsConfirmed, "the bubble shows the confirmation");
        Assert.Equal(("0xbeef", "Received"), (bubble.TxHash, bubble.StatusText));
    }

    [Fact]
    public async Task A_refused_send_is_marked_failed_with_the_servers_reason()
    {
        var alice = await app.CreateUserAsync("Alice Refused");
        var bob = await app.CreateUserAsync("Bob Refused");
        await using var aliceApp = await ClientHarness.SignInAsync(app, alice);
        var chat = await aliceApp.Api.CreateConversationAsync(new CreateConversationRequest(ConversationType.Direct, [bob.Id], null));
        var aliceChat = aliceApp.Chat();
        await aliceChat.OnNavigatedToAsync(Open(chat.Id));

        aliceChat.Draft = new string('x', 4001);
        await aliceChat.SendTextCommand.ExecuteAsync(null);

        var failed = aliceApp.Ui.Read(() => Assert.Single(aliceChat.Messages));
        Assert.Equal(DeliveryStatus.Failed, failed.Delivery);
        Assert.Equal("Messages are limited to 4000 characters.", failed.DeliveryError);
    }

    [Fact]
    public async Task Access_tokens_are_refreshed_before_they_expire()
    {
        var user = await app.CreateUserAsync("Refresh Client");
        await using var client = await ClientHarness.SignInAsync(app, user);

        var first = await client.Auth.GetAccessTokenAsync();
        var forced = await client.Auth.GetAccessTokenAsync(forceRefresh: true);
        Assert.NotNull(forced);
        Assert.NotEqual(first, forced);
        Assert.Equal(user.Id, (await client.Api.GetMeAsync()).Id);
    }

    [Fact]
    public async Task Shamwaris_add_accept_and_open_a_chat_to_send_money()
    {
        var alice = await app.CreateUserAsync("Alice Contacts");
        var bobEmail = $"bob.{Guid.NewGuid():N}@example.test";
        var bob = await app.CreateUserAsync("Bob Contacts", bobEmail);
        await using var aliceApp = await ClientHarness.SignInAsync(app, alice);
        await using var bobApp = await ClientHarness.SignInAsync(app, bob);
        var aliceContacts = aliceApp.Contacts();
        var bobContacts = bobApp.Contacts();
        await aliceContacts.LoadCommand.ExecuteAsync(null);
        await bobContacts.LoadCommand.ExecuteAsync(null);

        Assert.False(aliceContacts.AddShamwariCommand.CanExecute(null));
        aliceContacts.NewContact = bobEmail;
        await aliceContacts.AddShamwariCommand.ExecuteAsync(null);
        Assert.Equal(("Request sent to Bob Contacts.", ""), (aliceContacts.AddStatus, aliceContacts.NewContact));
        Assert.Equal("Bob Contacts", Assert.Single(aliceContacts.Outgoing).Name);

        await Wait.UntilAsync(() => bobApp.Ui.Read(() => bobContacts.HasIncoming), "Bob's list shows the request live");
        var request = bobApp.Ui.Read(() => Assert.Single(bobContacts.Incoming));
        Assert.Equal("Alice Contacts", request.Name);
        await bobContacts.AcceptCommand.ExecuteAsync(request);
        Assert.Equal("Alice Contacts", Assert.Single(bobContacts.Shamwaris).Name);

        await Wait.UntilAsync(() => aliceApp.Ui.Read(() => aliceContacts.HasShamwaris && !aliceContacts.HasOutgoing), "Alice's list shows Bob as a Shamwari live");
        var shamwari = aliceApp.Ui.Read(() => Assert.Single(aliceContacts.Shamwaris));
        await aliceContacts.SelectCommand.ExecuteAsync(shamwari);
        Assert.Equal([Routes.Chat], aliceApp.Navigator.Routes);

        var chat = Assert.Single(await aliceApp.Api.GetConversationsAsync());
        var aliceChat = aliceApp.Chat();
        await aliceChat.OnNavigatedToAsync(Open(chat.Id));
        Assert.Contains("Send money", aliceChat.Actions.Select(a => a.Title));
    }

    [Fact]
    public async Task A_post_is_published_liked_and_minted_through_the_view_models()
    {
        var alice = await app.CreateUserAsync("Alice Feed");
        var bob = await app.CreateUserAsync("Bob Feed");
        await using var aliceApp = await ClientHarness.SignInAsync(app, alice);
        await using var bobApp = await ClientHarness.SignInAsync(app, bob);
        var aliceFeed = aliceApp.Feed();
        await aliceFeed.RefreshCommand.ExecuteAsync(null);
        Assert.True(aliceFeed.MintingEnabled);

        var compose = aliceApp.Compose(aliceFeed);
        await compose.PublishCommand.ExecuteAsync(null);
        Assert.Equal("Add at least one photo.", compose.ErrorMessage);
        Assert.False(compose.AddPhoto("not a photo"u8.ToArray()));
        Assert.True(compose.AddPhoto(CowPhotos.Face(7, 1200, 900)));
        compose.Caption = "Market day in Mbare";
        await compose.PublishCommand.ExecuteAsync(null);

        Assert.True(aliceApp.Navigator.WentBack);
        var mine = aliceFeed.Posts[0];
        Assert.Equal(("Market day in Mbare", true, true, true), (mine.Caption, mine.IsMine, mine.CanMint, mine.CanDelete));

        // Bob sees it and likes it.
        var bobFeed = bobApp.Feed();
        await bobFeed.RefreshCommand.ExecuteAsync(null);
        var seen = bobFeed.Posts.First(p => p.Id == mine.Id);
        Assert.Equal((false, false), (seen.CanMint, seen.CanDelete));
        await bobFeed.ToggleLikeCommand.ExecuteAsync(seen);
        Assert.Equal(("1 like", true), (seen.LikeText, seen.LikedByMe));

        // Alice mints; Ndeipi confirms; her feed shows the NFT without a reload.
        await aliceFeed.MintCommand.ExecuteAsync(mine);
        Assert.Equal(("Queued for minting", false, false), (mine.NftText, mine.CanMint, mine.CanDelete));
        var claimed = await app.SqlAsync("EXEC ndeipi.usp_ClaimTokenTransfers @WorkerId = 'ndeipi-1', @BatchSize = 50");
        var row = claimed.Single(r => (Guid?)r["PostId"] == mine.Id);
        await app.SqlAsync("EXEC ndeipi.usp_CompleteTokenTransfer @Id = @id, @WorkerId = 'ndeipi-1', @TxHash = '0xabcdef0123456789'",
            new SqlParameter("@id", row["Id"]));
        await Wait.UntilAsync(() => aliceApp.Ui.Read(() => mine.IsMinted), "Alice's post shows as minted");
        Assert.StartsWith("NFT #…", aliceApp.Ui.Read(() => mine.NftText));
    }

    [Fact]
    public void Kinds_this_build_does_not_know_render_as_a_placeholder()
    {
        var extensions = new ChatExtensions([new TextMessageRenderer()], []);
        var message = new MessageDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "poll", ContractJson.ToElement(new { question = "Tea?" }), null, DateTimeOffset.UtcNow, Guid.NewGuid());

        var vm = extensions.Create(message, new MessageRenderContext(Guid.NewGuid(), _ => null));

        Assert.IsType<UnsupportedMessageViewModel>(vm);
        Assert.Equal("[Message]", vm.Preview);
    }
}

/// <summary>One signed-in copy of the app's non-UI code, wired to the test server.</summary>
public sealed class ClientHarness : IAsyncDisposable
{
    ClientHarness(AuthService auth, ChatApi api, ChatSession session)
    {
        Auth = auth;
        Api = api;
        Session = session;
        Extensions = new ChatExtensions(
            [new TextMessageRenderer(), new AssetTransferRenderer(), new BankTransferRenderer()],
            [new SendTokenAction(Navigator), new SendMoneyAction(Navigator)]);
    }

    public AuthService Auth { get; }
    public ChatApi Api { get; }
    public ChatSession Session { get; }
    public ChatExtensions Extensions { get; }
    public RecordingNavigator Navigator { get; } = new();
    public RecordingDialogs Dialogs { get; } = new();
    public LockingDispatcher Ui { get; } = new();

    public static async Task<ClientHarness> SignInAsync(TestApp app, TestUser user)
    {
        var options = new ClientOptions { ApiBaseUrl = app.Server.BaseAddress, RedirectUri = TestApp.RedirectUri };
        var auth = new AuthService(
            new HttpClient(app.Server.CreateHandler()) { BaseAddress = options.ApiBaseUrl },
            new InMemoryTokenStore(),
            new SimulatedClerkSignIn(app, user),
            options,
            TimeProvider.System);
        await auth.SignInAsync();

        var api = new ChatApi(new HttpClient(new AuthHeaderHandler(auth) { InnerHandler = app.Server.CreateHandler() }) { BaseAddress = options.ApiBaseUrl });
        var connection = new ChatConnection(options, auth, o =>
        {
            o.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
            o.Transports = HttpTransportType.LongPolling;
        });
        var session = new ChatSession(connection, api, auth);
        await session.StartAsync();
        await Wait.UntilAsync(() => connection.State == HubConnectionState.Connected, "the app connects to the hub");
        return new ClientHarness(auth, api, session);
    }

    public ChatsViewModel Chats() => new(Api, Session, Extensions, Navigator, Ui, Dialogs, TimeProvider.System);
    public ChatViewModel Chat() => new(Api, Session, Extensions, Ui, Dialogs, TimeProvider.System);
    public AssetTransferViewModel AssetTransfer() => new(Api, Session, Navigator);
    public ContactsViewModel Contacts() => new(Api, Session, Navigator, Dialogs, Ui);
    public FeedViewModel Feed() => new(Api, Session, Navigator, Dialogs, Ui, TimeProvider.System);
    public ComposePostViewModel Compose(FeedViewModel feed) => new(Api, feed, Navigator);

    public async ValueTask DisposeAsync()
    {
        await Session.StopAsync();
        await Session.Connection.DisposeAsync();
    }
}

/// <summary>
/// Stands in for the system browser: loads the real sign-in page, then does what clerk-js does
/// there once Clerk has a session -- trades the session token for a one-time code.
/// </summary>
sealed class SimulatedClerkSignIn(TestApp app, TestUser user) : IBrowserAuthenticator
{
    public async Task<IReadOnlyDictionary<string, string>> AuthenticateAsync(Uri url, Uri callbackUri, CancellationToken ct)
    {
        using var browser = app.CreateClient();
        using (var page = await browser.GetAsync(url, ct))
            page.EnsureSuccessStatusCode();

        var query = QueryHelpers.ParseQuery(url.Query);
        using var clerkPage = app.ClientWithToken(TestTokens.Create(user.ClerkId, user.SessionId, azp: TestApp.TrustedOrigin));
        using var response = await clerkPage.PostAsJsonAsync("api/auth/mobile/complete",
            new { redirectUri = query["redirect_uri"].ToString(), codeChallenge = query["code_challenge"].ToString() }, ct);
        response.EnsureSuccessStatusCode();
        var code = (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("code").GetString()!;
        return new Dictionary<string, string> { ["code"] = code, ["state"] = query["state"].ToString() };
    }
}

public sealed class RecordingNavigator : INavigator
{
    public List<string> Routes { get; } = [];
    public bool WentBack { get; private set; }

    public Task GoToAsync(string route, IDictionary<string, object>? parameters = null)
    {
        Routes.Add(route);
        return Task.CompletedTask;
    }

    public Task GoBackAsync()
    {
        WentBack = true;
        return Task.CompletedTask;
    }

    public Task ShowMainAsync() => Task.CompletedTask;
    public Task ShowSignInAsync() => Task.CompletedTask;
}

public sealed class RecordingDialogs : IDialogs
{
    public List<string> Alerts { get; } = [];

    public Task AlertAsync(string title, string message)
    {
        Alerts.Add($"{title}: {message}");
        return Task.CompletedTask;
    }

    public Task OpenBrowserAsync(Uri url) => Task.CompletedTask;
}

/// <summary>
/// Stands in for the UI thread: SignalR callbacks run under a lock, and the tests read view
/// models under the same lock, so they never see a collection mid-change.
/// </summary>
public sealed class LockingDispatcher : IUiDispatcher
{
    readonly Lock _gate = new();

    public void Post(Action action)
    {
        lock (_gate)
            action();
    }

    public T Read<T>(Func<T> read)
    {
        lock (_gate)
            return read();
    }
}
