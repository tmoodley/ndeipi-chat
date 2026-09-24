using Microsoft.EntityFrameworkCore;
using Ndeipi.Api.Assets;
using Ndeipi.Api.Auth;
using Ndeipi.Api.Banking;
using Ndeipi.Api.Chat;
using Ndeipi.Api.Data;
using Ndeipi.Api.Livestock;
using NdeipiChat.Contracts;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ChatDbContext>((sp, o) =>
    o.UseSqlServer(sp.GetRequiredService<IConfiguration>().GetConnectionString("Chat")));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.ConfigureHttpJsonOptions(o => ContractJson.Configure(o.SerializerOptions));
builder.Services.AddProblemDetails();

builder.Services.AddClerkAuthentication(builder.Configuration);
builder.Services.AddChat();
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

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapMobileAuth();
app.MapChat();
app.MapTokenTransfers();
app.MapBanking();
app.MapLivestock();

app.Run();

public partial class Program;
