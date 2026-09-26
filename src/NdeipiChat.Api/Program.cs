using Microsoft.EntityFrameworkCore;
using NdeipiChat.Api;
using NdeipiChat.Api.Assets;
using NdeipiChat.Api.Auth;
using NdeipiChat.Api.Banking;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Api.Livestock;
using NdeipiChat.Api.Shamwaris;
using NdeipiChat.Api.Social;
using NdeipiChat.Contracts;

var builder = WebApplication.CreateBuilder(args);

// Clerk's handshake token rides in the sign-in page's query string; see web.config for IIS's limit.
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestLineSize = 16 * 1024);

builder.Services.AddDbContext<ChatDbContext>((sp, o) =>
    o.UseSqlServer(sp.GetRequiredService<IConfiguration>().GetConnectionString("Chat")));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.ConfigureHttpJsonOptions(o => ContractJson.Configure(o.SerializerOptions));
builder.Services.AddProblemDetails();
builder.Services.AddApiDocs();

builder.Services.AddClerkAuthentication(builder.Configuration);
builder.Services.AddChat();
builder.Services.AddShamwaris();
builder.Services.AddSocial(builder.Configuration);
builder.Services.AddTokenTransfers(builder.Configuration);
builder.Services.AddBridgeBanking(builder.Configuration);
builder.Services.AddLivestock(builder.Configuration);

var app = builder.Build();

if (app.Configuration.GetValue("Database:MigrateOnStartup", false))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<ChatDbContext>().Database.MigrateAsync();
}

app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseAuthentication();
app.UseAuthorization();

app.MapApiDocs();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapMobileAuth();
app.MapChat();
app.MapShamwaris();
app.MapSocial();
app.MapTokenTransfers();
app.MapBanking();
app.MapLivestock();

// The web app (NdeipiChat.Web): Blazor WebAssembly, served from the same origin as the API.
// MapStaticAssets, not UseStaticFiles: it fills index.html's fingerprinted file names
// (blazor.webassembly#[.{fingerprint}].js) as it serves the page; the published file keeps the
// placeholders.
app.MapStaticAssets();

// Any other path is a page of the web app -- except under the API's own prefixes, where an
// unknown path stays a 404 rather than turning into the app's HTML.
foreach (var prefix in new[] { "api", "hubs", "auth", "webhooks", "openapi", "media", "nft" })
    app.Map($"{prefix}/{{**path}}", () => Results.NotFound());
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
