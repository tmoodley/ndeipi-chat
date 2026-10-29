using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.CyberWars;

/// <summary>
/// The war-room commands (FR-2.2b). /reinforce and /vote-strike act on the game with play credits
/// and units only. /send-coop moves nothing: it answers with a card that opens the chat's own
/// transfer screen, filled in, where the player checks and confirms it themselves.
/// </summary>
public sealed class CyberWarsWarRoom(ChatDbContext db, CyberWarsMatches matches, CyberWarsCards cards, CyberWarsWorldService world, TimeProvider clock) : IChatCommandListener
{
    public static readonly TimeSpan ReinforceEvery = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan StrikeLasts = TimeSpan.FromHours(1);
    public const int ReinforcementSquads = 2;

    /// <summary>What an approved strike adds to raids on its sector (MatchEngine.NewRaid), as the cards describe it.</summary>
    public const int StrikeSquads = 2;
    public const int StrikeOre = 1000;

    /// <summary>The token /send-coop fills in: Coop Coin, moved only by the wallet.</summary>
    public const string CoopSymbol = CyberWarsContract.CoopSymbol;

    public async Task OnCommandAsync(MessageContext context, string command, string arguments, CancellationToken ct)
    {
        if (command is not ("/reinforce" or "/vote-strike" or "/send-coop"))
            return;
        var alliance = await db.CyberWarsAlliances.FirstOrDefaultAsync(a => a.ConversationId == context.Conversation.Id, ct);
        if (alliance is null)
            return;
        var members = await db.CyberWarsAllianceMembers.Where(m => m.AllianceId == alliance.Id).ToListAsync(ct);
        if (members.All(m => m.UserId != context.Sender.Id))
            return;

        var answer = command switch
        {
            "/reinforce" => await ReinforceAsync(context, members, arguments, ct),
            "/vote-strike" => await VoteStrikeAsync(context, alliance, arguments, ct),
            _ => SendCoop(context, members, arguments),
        };
        if (answer is not null)
            await cards.PostAsync(context.Sender, context.Conversation.Id, answer, ct);
    }

    static CyberWarsCardPayload Note(string icon, string title, string text) =>
        new(icon, title, text, $"apps/{CyberWarsContract.AppId}", "Open CyberWars");

    /// <summary>The member a "@Name" argument names, matched on their display name.</summary>
    static User? Mentioned(MessageContext context, IEnumerable<CyberWarsAllianceMember> members, string text)
    {
        var name = text.Trim().TrimStart('@').Trim();
        if (name.Length == 0)
            return null;
        var ids = members.Select(m => m.UserId).ToHashSet();
        return context.Members.Where(u => ids.Contains(u.Id))
            .FirstOrDefault(u => string.Equals(u.DisplayName, name, StringComparison.OrdinalIgnoreCase))
            ?? context.Members.Where(u => ids.Contains(u.Id))
                .FirstOrDefault(u => u.DisplayName.StartsWith(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>/reinforce @user: two security squads land beside an ally who is in a raid, once per member every 5 minutes.</summary>
    async Task<CyberWarsCardPayload> ReinforceAsync(MessageContext context, List<CyberWarsAllianceMember> members, string arguments, CancellationToken ct)
    {
        if (Mentioned(context, members, arguments) is not { } ally)
            return Note("❔", "Who should get reinforcements?", "Try /reinforce @name, naming someone in this alliance.");
        var me = members.Single(m => m.UserId == context.Sender.Id);
        var now = clock.GetUtcNow();
        if (me.LastReinforceAt is { } last && now - last < ReinforceEvery)
            return Note("⏳", "Reinforcements are on their way back", $"You can send more at {(last + ReinforceEvery).UtcDateTime:HH:mm} UTC.");

        var running = await db.CyberWarsMatchPlayers
            .Where(p => p.UserId == ally.Id && p.Side != MatchSides.Solo)
            .Join(db.CyberWarsMatches.Where(m => m.Status == MatchStatuses.Running), p => p.MatchId, m => m.Id, (p, m) => m.Id)
            .ToListAsync(ct);
        foreach (var matchId in running)
        {
            if (await matches.GetAsync(matchId, db, ct) is not { } live)
                continue;
            bool landed;
            lock (live.Gate)
                landed = live.Engine.Reinforce(ally.Id, ReinforcementSquads, $"{context.Sender.DisplayName} sent reinforcements.");
            if (!landed)
                continue;
            me.LastReinforceAt = now;
            await db.SaveChangesAsync(ct);
            return Note("🛡️", $"Reinforcements for {ally.DisplayName}", $"{ReinforcementSquads} security squads from {context.Sender.DisplayName} have landed.");
        }
        return Note("❔", $"{ally.DisplayName} isn't in a raid", "Reinforcements can only reach someone raiding or defending right now.");
    }

    /// <summary>
    /// /vote-strike Province q,r opens a vote on a sector; /vote-strike on its own votes for it. With
    /// more than half the alliance behind it, members' raids there land with extra squads and credits
    /// for an hour. Either way the answer is the vote's own card, updated in place; a new card only
    /// says what went wrong.
    /// </summary>
    async Task<CyberWarsCardPayload?> VoteStrikeAsync(MessageContext context, CyberWarsAlliance alliance, string arguments, CancellationToken ct)
    {
        if (arguments.Length > 0)
        {
            var parts = arguments.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
            if (parts is not [var p, var q, var r] || !CyberWarsWorld.TryParseSector($"{p}:{q}:{r}", out var province, out var sector))
                return Note("❔", "Which sector?", "Try /vote-strike Lusaka 1,-1 to open a vote, or /vote-strike to vote for the open one.");
            await world.OpenStrikeAsync(context.Sender, alliance, province, sector, ct);
            return null;
        }
        if (!await db.CyberWarsStrikes.AnyAsync(s => s.AllianceId == alliance.Id, ct))
            return Note("❔", "No strike vote is open", "Open one with /vote-strike Lusaka 1,-1.");
        await world.VoteStrikeAsync(context.Sender, alliance, ct);
        return null;
    }

    /// <summary>
    /// /send-coop 50 @user: never moves value from a chat line. It answers with a card that opens the
    /// wallet's transfer screen with the amount and recipient filled in; nothing moves unless the
    /// sender confirms it there, where the wallet's own checks (KYC included) apply.
    /// </summary>
    static CyberWarsCardPayload SendCoop(MessageContext context, List<CyberWarsAllianceMember> members, string arguments)
    {
        var words = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var amountText = words.FirstOrDefault(w => decimal.TryParse(w, NumberStyles.Number, CultureInfo.InvariantCulture, out _));
        var who = string.Join(' ', words.Where(w => w != amountText));
        if (amountText is null || !decimal.TryParse(amountText, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) || amount <= 0 || amount > 1_000_000)
            return Note("❔", "How much Coop Coin?", "Try /send-coop 50 @name.");
        var recipient = Mentioned(context, members, who);
        if (recipient is null || recipient.Id == context.Sender.Id)
            return Note("❔", "Send Coop Coin to whom?", "Name someone else in this alliance: /send-coop 50 @name.");
        var amountShown = amount.ToString("0.########", CultureInfo.InvariantCulture);
        var route = $"chat/{context.Conversation.Id}/transfer?to={recipient.Id}&amount={amountShown}&symbol={CoopSymbol}";
        return new CyberWarsCardPayload("🪙", $"Send {amountShown} Coop Coin to {recipient.DisplayName}?",
            "Nothing has moved. Open your wallet to check the amount and recipient, then confirm.", route, "Confirm in wallet");
    }
}
