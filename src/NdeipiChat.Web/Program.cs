using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using NdeipiChat.Client;
using NdeipiChat.Client.Livestock;
using NdeipiChat.Web;
using NdeipiChat.Web.Platform;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Served by NdeipiChat.Api, so the API is this page's own origin.
var origin = new Uri(builder.HostEnvironment.BaseAddress);
builder.Services.AddNdeipiChatClient(new ClientOptions
{
    ApiBaseUrl = origin,
    RedirectUri = new Uri(origin, WebNavigator.CallbackPath).ToString(),
    DeviceName = "Web browser",
    SupportsLivestock = false
});

builder.Services.AddSingleton<BrowserStorage>();
builder.Services.AddSingleton<ITokenStore, BrowserTokenStore>();
builder.Services.AddSingleton<IBrowserAuthenticator, RedirectOnlyAuthenticator>();
builder.Services.AddSingleton<WebSignIn>();
builder.Services.AddSingleton<INavigator, WebNavigator>();
builder.Services.AddSingleton<IDialogs, WebDialogs>();
builder.Services.AddSingleton<WebDispatcher>();
builder.Services.AddSingleton<IUiDispatcher>(sp => sp.GetRequiredService<WebDispatcher>());

// The livestock services come with the client but aren't used on the web; these only let them be
// constructed.
builder.Services.AddSingleton<IOperatorKeyStore, InMemoryOperatorKeyStore>();
builder.Services.AddSingleton<ILocationProvider, NoLocationProvider>();
builder.Services.AddSingleton<ISettingsStore, InMemorySettingsStore>();

await builder.Build().RunAsync();
