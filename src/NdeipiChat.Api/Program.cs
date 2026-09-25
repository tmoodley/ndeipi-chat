using Microsoft.EntityFrameworkCore;
using NdeipiChat.Api;
using NdeipiChat.Api.Assets;
using NdeipiChat.Api.Auth;
using NdeipiChat.Api.Banking;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Api.Livestock;
using NdeipiChat.Api.Shamwaris;
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
app.MapTokenTransfers();
app.MapBanking();
app.MapLivestock();

app.Run();

public partial class Program;
