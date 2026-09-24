using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.SqlClient;
using NdeipiChat.Contracts;
using NdeipiChat.Tests.Infrastructure;

namespace NdeipiChat.Tests;

/// <summary>
/// Token transfers end to end, with the tests playing Ndeipi Enterprise Server through the
/// stored procedures it's meant to use.
/// </summary>
public sealed class AssetTransferTests(TestApp app) : IClassFixture<TestApp>
{
    const string NftContract = "0x2222222222222222222222222222222222222222";

    static TokenRef Nmx(string symbol = "NMX") => new("polygon", symbol, TokenStandards.Erc20, TestApp.KnownTokenContract, null, null);

    static SendMessageRequest Transfer(Guid chatId, Guid to, TokenRef token, string amount, string? memo = null) =>
        new(chatId, MessageKinds.AssetTransfer, ContractJson.ToElement(new AssetTransferPayload(to, token, amount, memo)), Guid.NewGuid());

    static AssetTransferState StateOf(JsonElement state) => ContractJson.Read<AssetTransferState>(state)!;

    async Task<(TestUser Alice, TestUser Bob, ConversationDto Chat)> PairAsync()
    {
        var alice = await app.CreateUserAsync("Alice Tokens");
        var bob = await app.CreateUserAsync("Bob Tokens");
        return (alice, bob, await alice.StartDirectChatAsync(bob));
    }

    Task<List<Dictionary<string, object?>>> ClaimAsync(string worker, int batch = 100) =>
        app.SqlAsync("EXEC ndeipi.usp_ClaimTokenTransfers @WorkerId = @w, @BatchSize = @b", new SqlParameter("@w", worker), new SqlParameter("@b", batch));

    [Fact]
    public async Task A_transfer_is_queued_for_Ndeipi_together_with_its_message()
    {
        var (alice, bob, chat) = await PairAsync();
        (await alice.Http.PutAsJsonAsync("api/me/wallets", new UserWalletDto("Polygon", "0xA11CE"), ContractJson.Options)).EnsureSuccessStatusCode();
        (await bob.Http.PutAsJsonAsync("api/me/wallets", new UserWalletDto("polygon", "0xB0B"), ContractJson.Options)).EnsureSuccessStatusCode();

        // The app labels a known contract "USDT"; the server's own catalogue decides the symbol.
        var message = await alice.PostAsync<MessageDto>($"api/conversations/{chat.Id}/messages",
            Transfer(chat.Id, bob.Id, Nmx("USDT"), "12.50", "  lunch  "));

        var state = StateOf(message.State!.Value);
        Assert.Equal(TransferStatuses.Pending, state.Status);
        var payload = ContractJson.Read<AssetTransferPayload>(message.Payload)!;
        Assert.Equal("NMX", payload.Token.Symbol);
        Assert.Equal("12.5", payload.Amount);
        Assert.Equal("lunch", payload.Memo);

        var row = (await app.SqlAsync("SELECT * FROM ndeipi.TokenTransferQueue WHERE Id = @id", new SqlParameter("@id", state.TransferId))).Single();
        Assert.Equal("Transfer", row["Operation"]);
        Assert.Equal("Pending", row["Status"]);
        Assert.Equal("polygon", row["Chain"]);
        Assert.Equal("erc20", row["TokenStandard"]);
        Assert.Equal("NMX", row["TokenSymbol"]);
        Assert.Equal(TestApp.KnownTokenContract, row["ContractAddress"]);
        Assert.Equal(6, row["Decimals"]);
        Assert.Equal(12.5m, row["Amount"]);
        Assert.Equal(alice.Id, row["SenderUserId"]);
        Assert.Equal(alice.ClerkId, row["SenderClerkId"]);
        Assert.Equal("0xA11CE", row["SenderWalletAddress"]);
        Assert.Equal(bob.Id, row["RecipientUserId"]);
        Assert.Equal(bob.ClerkId, row["RecipientClerkId"]);
        Assert.Equal("0xB0B", row["RecipientWalletAddress"]);
        Assert.Equal(message.Id, row["MessageId"]);
        Assert.Equal(chat.Id, row["ConversationId"]);
        Assert.Equal("lunch", row["Memo"]);
    }

    [Fact]
    public async Task Ndeipi_status_changes_reach_both_people()
    {
        var (alice, bob, chat) = await PairAsync();
        await using var aliceHub = await app.ConnectAsync(alice);
        await using var bobHub = await app.ConnectAsync(bob);

        var message = await alice.PostAsync<MessageDto>($"api/conversations/{chat.Id}/messages", Transfer(chat.Id, bob.Id, Nmx(), "5"));
        var transferId = StateOf(message.State!.Value).TransferId;

        var processing = Wait.ForEventAsync<MessageStateDto>(bobHub, nameof(IChatClient.MessageStateChanged),
            s => s.MessageId == message.Id && StateOf(s.State).Status == TransferStatuses.Processing);
        var claimed = await ClaimAsync("ndeipi-1");
        var mine = claimed.Single(r => (Guid)r["Id"]! == transferId);
        Assert.Equal("Processing", mine["Status"]);
        Assert.Equal("ndeipi-1", mine["ClaimedBy"]);
        await processing;

        var confirmedForAlice = Wait.ForEventAsync<MessageStateDto>(aliceHub, nameof(IChatClient.MessageStateChanged),
            s => s.MessageId == message.Id && StateOf(s.State).Status == TransferStatuses.Confirmed);
        var confirmedForBob = Wait.ForEventAsync<MessageStateDto>(bobHub, nameof(IChatClient.MessageStateChanged),
            s => s.MessageId == message.Id && StateOf(s.State).Status == TransferStatuses.Confirmed);
        await app.SqlAsync("EXEC ndeipi.usp_CompleteTokenTransfer @Id = @id, @WorkerId = @w, @TxHash = @tx",
            new SqlParameter("@id", transferId), new SqlParameter("@w", "ndeipi-1"), new SqlParameter("@tx", "0xfeed"));

        Assert.Equal("0xfeed", StateOf((await confirmedForAlice).State).TxHash);
        await confirmedForBob;

        var history = await bob.GetAsync<List<MessageDto>>($"api/conversations/{chat.Id}/messages");
        Assert.Equal(TransferStatuses.Confirmed, StateOf(history.Single().State!.Value).Status);
    }

    [Fact]
    public async Task A_transfer_Ndeipi_rejects_shows_the_reason()
    {
        var (alice, bob, chat) = await PairAsync();
        await using var bobHub = await app.ConnectAsync(bob);
        var message = await alice.PostAsync<MessageDto>($"api/conversations/{chat.Id}/messages", Transfer(chat.Id, bob.Id, Nmx(), "1000000"));
        var transferId = StateOf(message.State!.Value).TransferId;
        await ClaimAsync("ndeipi-2");

        var failed = Wait.ForEventAsync<MessageStateDto>(bobHub, nameof(IChatClient.MessageStateChanged),
            s => s.MessageId == message.Id && StateOf(s.State).Status == TransferStatuses.Failed);
        await app.SqlAsync("EXEC ndeipi.usp_FailTokenTransfer @Id = @id, @WorkerId = @w, @Error = @e",
            new SqlParameter("@id", transferId), new SqlParameter("@w", "ndeipi-2"), new SqlParameter("@e", "Insufficient NMX balance"));

        Assert.Equal("Insufficient NMX balance", StateOf((await failed).State).Error);
    }

    [Fact]
    public async Task Parallel_workers_never_claim_the_same_transfer()
    {
        var (alice, bob, chat) = await PairAsync();
        await ClaimAsync("drain", 1000);

        var mine = new List<Guid>();
        for (var i = 0; i < 12; i++)
        {
            var message = await alice.PostAsync<MessageDto>($"api/conversations/{chat.Id}/messages", Transfer(chat.Id, bob.Id, Nmx(), "1"));
            mine.Add(StateOf(message.State!.Value).TransferId);
        }

        var rounds = await Task.WhenAll(Enumerable.Range(1, 4).Select(w => ClaimAsync($"worker-{w}", batch: 5)));
        var stragglers = await ClaimAsync("worker-5");
        var claimed = rounds.SelectMany(r => r).Concat(stragglers).Select(r => (Guid)r["Id"]!).ToList();

        Assert.Equal(claimed.Count, claimed.Distinct().Count());
        Assert.All(mine, id => Assert.Single(claimed, c => c == id));
    }

    [Fact]
    public async Task Only_the_worker_holding_a_transfer_can_finish_it()
    {
        var (alice, bob, chat) = await PairAsync();
        var message = await alice.PostAsync<MessageDto>($"api/conversations/{chat.Id}/messages", Transfer(chat.Id, bob.Id, Nmx(), "2"));
        var transferId = StateOf(message.State!.Value).TransferId;

        // Still Pending: nobody has claimed it.
        var unclaimed = await Assert.ThrowsAsync<SqlException>(() => app.SqlAsync("EXEC ndeipi.usp_CompleteTokenTransfer @Id = @id, @WorkerId = @w, @TxHash = @tx",
            new SqlParameter("@id", transferId), new SqlParameter("@w", "ndeipi-3"), new SqlParameter("@tx", "0x1")));
        Assert.Contains("isn't being processed by ndeipi-3", unclaimed.Message);

        await ClaimAsync("ndeipi-3");
        var otherWorker = await Assert.ThrowsAsync<SqlException>(() => app.SqlAsync("EXEC ndeipi.usp_CompleteTokenTransfer @Id = @id, @WorkerId = @w, @TxHash = @tx",
            new SqlParameter("@id", transferId), new SqlParameter("@w", "ndeipi-4"), new SqlParameter("@tx", "0x1")));
        Assert.Contains("isn't being processed by ndeipi-4", otherWorker.Message);

        // Released untouched, it goes back to the queue for any worker.
        await app.SqlAsync("EXEC ndeipi.usp_ReleaseTokenTransfer @Id = @id, @WorkerId = @w", new SqlParameter("@id", transferId), new SqlParameter("@w", "ndeipi-3"));
        var row = (await app.SqlAsync("SELECT Status, ClaimedBy FROM ndeipi.TokenTransferQueue WHERE Id = @id", new SqlParameter("@id", transferId))).Single();
        Assert.Equal("Pending", row["Status"]);
        Assert.Null(row["ClaimedBy"]);
    }

    [Fact]
    public async Task An_nft_transfer_carries_its_token_id()
    {
        var (alice, bob, chat) = await PairAsync();
        const string maxUint256 = "115792089237316195423570985008687907853269984665640564039457584007913129639935";
        var token = new TokenRef("ethereum", "KEYS", TokenStandards.Erc1155, NftContract, 18, maxUint256);

        var message = await alice.PostAsync<MessageDto>($"api/conversations/{chat.Id}/messages", Transfer(chat.Id, bob.Id, token, "3"));

        var row = (await app.SqlAsync("SELECT TokenId, Decimals, Amount, TokenStandard FROM ndeipi.TokenTransferQueue WHERE Id = @id",
            new SqlParameter("@id", StateOf(message.State!.Value).TransferId))).Single();
        Assert.Equal(maxUint256, row["TokenId"]);
        Assert.Null(row["Decimals"]);
        Assert.Equal(3m, row["Amount"]);
        Assert.Equal("erc1155", row["TokenStandard"]);
    }

    [Theory]
    [InlineData("0", TokenStandards.Erc20, null, "greater than zero")]
    [InlineData("-1", TokenStandards.Erc20, null, "greater than zero")]
    [InlineData("1e5", TokenStandards.Erc20, null, "greater than zero")]
    [InlineData("1.1234567", TokenStandards.Erc20, null, "at most 6 decimal places")]
    [InlineData("1", TokenStandards.Erc721, null, "id of the NFT")]
    [InlineData("2", TokenStandards.Erc721, "7", "one at a time")]
    [InlineData("1.5", TokenStandards.Erc1155, "7", "whole number")]
    [InlineData("1", "bitcoin-ordinal", null, "Unknown token standard")]
    public async Task Malformed_transfers_are_refused(string amount, string standard, string? tokenId, string expected)
    {
        var (alice, bob, chat) = await PairAsync();
        var contract = TokenStandards.IsNonFungible(standard) ? NftContract : TestApp.KnownTokenContract;
        var token = new TokenRef("polygon", "TKN", standard, contract, null, tokenId);

        using var response = await alice.Http.PostAsJsonAsync($"api/conversations/{chat.Id}/messages",
            Transfer(chat.Id, bob.Id, token, amount), ContractJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expected, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Transfers_go_to_someone_else_in_the_chat()
    {
        var (alice, bob, chat) = await PairAsync();
        var carol = await app.CreateUserAsync("Carol Elsewhere");

        using var toSelf = await alice.Http.PostAsJsonAsync($"api/conversations/{chat.Id}/messages", Transfer(chat.Id, alice.Id, Nmx(), "1"), ContractJson.Options);
        Assert.Contains("can't send to yourself", await toSelf.Content.ReadAsStringAsync());

        using var toOutsider = await alice.Http.PostAsJsonAsync($"api/conversations/{chat.Id}/messages", Transfer(chat.Id, carol.Id, Nmx(), "1"), ContractJson.Options);
        Assert.Contains("isn't in this conversation", await toOutsider.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_app_is_offered_the_servers_token_catalogue()
    {
        var alice = await app.CreateUserAsync("Alice Catalogue");
        var tokens = await alice.GetAsync<List<TokenRef>>("api/tokens");
        var nmx = Assert.Single(tokens);
        Assert.Equal(("polygon", "NMX", 6), (nmx.Chain, nmx.Symbol, nmx.Decimals));
    }
}
