using System.Globalization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Ndeipi.Api.Auth;
using Ndeipi.Api.Banking;
using Ndeipi.Api.Data;
using NdeipiChat.Contracts;

namespace Ndeipi.Api.Chat;

public static class ChatModule
{
    public static IServiceCollection AddChat(this IServiceCollection services)
    {
        services.AddSignalR().AddJsonProtocol(o => ContractJson.Configure(o.PayloadSerializerOptions));
        services.AddSingleton<ChatNotifier>();
        services.AddScoped<MessageService>();
        services.AddScoped<MessageStateService>();
        services.AddScoped<ConversationService>();
        services.AddMessageKind<TextMessageHandler>();
        services.AddExceptionHandler<ApiExceptionHandler>();
        return services;
    }

    /// <summary>Adds a chat extension: the handler for one message kind.</summary>
    public static IServiceCollection AddMessageKind<THandler>(this IServiceCollection services)
        where THandler : class, IMessageKindHandler =>
        services.AddScoped<IMessageKindHandler, THandler>();

    public static void MapChat(this IEndpointRouteBuilder app)
    {
        app.MapHub<ChatHub>(ChatHubContract.Path);

        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/me", async (HttpContext http, CurrentUserService users, ChatDbContext db, BankingService banking) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            var wallets = await db.UserWallets.AsNoTracking()
                .Where(w => w.UserId == me.Id)
                .OrderBy(w => w.Chain)
                .Select(w => new UserWalletDto(w.Chain, w.Address))
                .ToListAsync(http.RequestAborted);
            var banking_ = await banking.GetStatusAsync(me.Id, http.RequestAborted);
            return Results.Ok(new MeDto(me.Id, me.DisplayName, me.Username, me.Email, me.AvatarUrl, wallets, banking_));
        });

        api.MapPut("/me/wallets", async (UserWalletDto wallet, HttpContext http, CurrentUserService users, ChatDbContext db, TimeProvider clock) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            var chain = wallet.Chain?.Trim().ToLowerInvariant() ?? "";
            var address = wallet.Address?.Trim() ?? "";
            if (chain.Length is 0 or > 50 || address.Length is 0 or > 128)
                throw new ChatRejectedException("Enter a chain and a wallet address.");

            var existing = await db.UserWallets.FirstOrDefaultAsync(w => w.UserId == me.Id && w.Chain == chain, http.RequestAborted);
            if (existing is null)
                db.UserWallets.Add(new UserWallet { Id = Guid.NewGuid(), UserId = me.Id, Chain = chain, Address = address, UpdatedAt = clock.GetUtcNow() });
            else
                (existing.Address, existing.UpdatedAt) = (address, clock.GetUtcNow());
            await db.SaveChangesAsync(http.RequestAborted);
            return Results.Ok(new UserWalletDto(chain, address));
        });

        api.MapDelete("/me/wallets/{chain}", async (string chain, HttpContext http, CurrentUserService users, ChatDbContext db) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            var key = chain.Trim().ToLowerInvariant();
            await db.UserWallets.Where(w => w.UserId == me.Id && w.Chain == key).ExecuteDeleteAsync(http.RequestAborted);
            return Results.NoContent();
        });

        api.MapGet("/users/search", async ([FromQuery] string? q, HttpContext http, CurrentUserService users, ChatDbContext db) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            q = q?.Trim();
            if (q is null || q.Length < 2)
                return Results.Ok(Array.Empty<UserDto>());

            // Names match partially; email only exactly, so the search can't be used to harvest addresses.
            var found = await db.Users.AsNoTracking()
                .Where(u => u.Id != me.Id && (u.DisplayName.Contains(q) || (u.Username != null && u.Username.Contains(q)) || u.Email == q))
                .OrderBy(u => u.DisplayName)
                .Take(20)
                .Select(u => new UserDto(u.Id, u.DisplayName, u.Username, u.AvatarUrl))
                .ToListAsync(http.RequestAborted);
            return Results.Ok(found);
        });

        api.MapGet("/users/{id:guid}", async (Guid id, ChatDbContext db, CancellationToken ct) =>
            await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct) is { } user
                ? Results.Ok(ChatMapper.ToDto(user))
                : Results.NotFound());

        api.MapGet("/conversations", async (HttpContext http, CurrentUserService users, ConversationService conversations) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return Results.Ok(await conversations.ListForAsync(me.Id, null, http.RequestAborted));
        });

        api.MapPost("/conversations", async (CreateConversationRequest request, HttpContext http, CurrentUserService users, ConversationService conversations) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return Results.Ok(await conversations.CreateAsync(me, request, http.RequestAborted));
        });

        api.MapGet("/conversations/{id:guid}", async (Guid id, HttpContext http, CurrentUserService users, ConversationService conversations) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            var found = await conversations.ListForAsync(me.Id, id, http.RequestAborted);
            return found.Count == 1 ? Results.Ok(found[0]) : Results.NotFound();
        });

        api.MapGet("/conversations/{id:guid}/messages", async (Guid id, Guid? before, int? take, HttpContext http, CurrentUserService users, ConversationService conversations) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return Results.Ok(await conversations.MessagesAsync(me.Id, id, before, take ?? 50, http.RequestAborted));
        });

        // The same pipeline as the hub's SendMessage, for callers without a SignalR connection.
        api.MapPost("/conversations/{id:guid}/messages", async (Guid id, SendMessageRequest request, HttpContext http, CurrentUserService users, MessageService messages) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return Results.Ok(await messages.SendAsync(me, request with { ConversationId = id }, http.RequestAborted));
        });
    }
}

/// <summary>Turns refusals into 400s with a message the app can show as-is.</summary>
public sealed class ApiExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        var (status, title) = exception switch
        {
            ChatRejectedException => (StatusCodes.Status400BadRequest, exception.Message),
            UnauthorizedAccessException => (StatusCodes.Status401Unauthorized, "Sign in again."),
            BridgeApiException => (StatusCodes.Status502BadGateway, "The banking provider couldn't complete the request."),
            _ => (0, "")
        };
        if (status == 0)
            return false;

        http.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails = new ProblemDetails { Status = status, Title = title }
        });
    }
}

public static class Amounts
{
    /// <summary>
    /// Parses a positive decimal string in invariant culture -- digits and one point, no signs,
    /// separators or exponents -- with at most <paramref name="maxDecimals"/> significant decimal places.
    /// </summary>
    public static bool TryParse(string? text, int maxDecimals, out decimal amount)
    {
        amount = 0;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 60)
            return false;
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount))
            return false;

        var point = text.IndexOf('.');
        var decimals = point < 0 ? 0 : text[(point + 1)..].TrimEnd('0').Length;
        return amount > 0 && decimals <= maxDecimals;
    }

    public static string Format(decimal amount) =>
        amount.ToString("0.############################", CultureInfo.InvariantCulture);
}
