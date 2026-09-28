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
// Sub-apps must be signed by a publisher key built into the shell (NFR-02-01); registered before
// the client so it wins over the default. Local development runs the bundles the API builds on the
// fly, which nothing has signed.
builder.Services.AddSingleton(SubAppTrust.Of(requireSignature: !builder.HostEnvironment.IsDevelopment(), PublisherKeys.All));
builder.Services.AddNdeipiChatClient(new ClientOptions
{
    ApiBaseUrl = origin,
    RedirectUri = new Uri(origin, WebNavigator.CallbackPath).ToString(),
    DeviceName = "Web browser"
});

builder.Services.AddSingleton<BrowserStorage>();
builder.Services.AddSingleton<ITokenStore, BrowserTokenStore>();
builder.Services.AddSingleton<IBrowserAuthenticator, RedirectOnlyAuthenticator>();
builder.Services.AddSingleton<WebSignIn>();
builder.Services.AddSingleton<WebEmbedding>();
builder.Services.AddSingleton<ShellUi>();
builder.Services.AddSingleton<SocialActions>();
builder.Services.AddSingleton<Microsoft.AspNetCore.Components.WebAssembly.Services.LazyAssemblyLoader>();
builder.Services.AddSingleton<SubAppLoader>();
builder.Services.AddSingleton<ShellRealtime>();
builder.Services.AddSingleton<INavigator, WebNavigator>();
builder.Services.AddSingleton<IDialogs, WebDialogs>();
builder.Services.AddSingleton<WebDispatcher>();
builder.Services.AddSingleton<IUiDispatcher>(sp => sp.GetRequiredService<WebDispatcher>());

// Livestock: the browser's versions of the phone's capture queue, key storage, signing and GPS.
builder.Services.AddSingleton<ILivestockCaptureQueue, IndexedDbCaptureQueue>();
builder.Services.AddSingleton<IP256Signer, WebCryptoP256Signer>();
builder.Services.AddSingleton<IOperatorKeyStore, BrowserOperatorKeyStore>();
builder.Services.AddSingleton<ILocationProvider, BrowserLocationProvider>();
builder.Services.AddSingleton<ISettingsStore, BrowserSettingsStore>();

await builder.Build().RunAsync();
