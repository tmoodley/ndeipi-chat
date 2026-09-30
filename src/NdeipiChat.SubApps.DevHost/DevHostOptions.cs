using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using NdeipiChat.SubApps.Sdk;

namespace NdeipiChat.SubApps.DevHost;

/// <summary>Someone the dev host can sign in as. Switch between them in the dev host's bar.</summary>
public sealed record DevUser(Guid Id, string DisplayName);

/// <summary>What the dev host pretends the shell and the Ndeipi API are.</summary>
public sealed class DevHostOptions
{
    internal DevHostOptions(string appId, Type rootType)
    {
        AppId = appId;
        RootType = rootType;
        Api = new DevApi(Realtime);
        Realtime.Json = Api.Json;
    }

    /// <summary>The id from the root component's <see cref="SubAppRootAttribute"/>.</summary>
    public string AppId { get; }

    public Type RootType { get; }

    /// <summary>The name in the dev host's bar; the app id if not set.</summary>
    public string? Title { get; set; }

    /// <summary>People to sign in as. The first is signed in to start with.</summary>
    public List<DevUser> Users { get; } =
    [
        new(Guid.Parse("7d3c1c9e-5a52-4d1b-9a55-3f1f6c2b0a01"), "Tendai Moyo"),
        new(Guid.Parse("7d3c1c9e-5a52-4d1b-9a55-3f1f6c2b0a02"), "Rudo Dube"),
    ];

    /// <summary>The query string the app is opened with, without "?", e.g. "chat=…". Editable in the bar.</summary>
    public string Query { get; set; } = "";

    /// <summary>The Ndeipi API as the app sees it: add a mock for each route the app calls.</summary>
    public DevApi Api { get; }

    /// <summary>Topics the app subscribes to. Mocks and the dev host's Realtime panel publish to them.</summary>
    public DevRealtime Realtime { get; } = new();

    /// <summary>False gives the app a null <see cref="SubAppContext.Realtime"/>, as a shell without one would.</summary>
    public bool OfferRealtime { get; set; } = true;

    /// <summary>False gives the app a null <see cref="SubAppContext.Device"/>, as a shell without one would.</summary>
    public bool OfferDevice { get; set; } = true;
}

public static class DevHostBuilderExtensions
{
    /// <summary>
    /// Runs <typeparamref name="TRoot"/>, a micro-app's root component, in the dev host. Call it
    /// instead of adding root components yourself.
    /// </summary>
    public static WebAssemblyHostBuilder AddSubAppDevHost<TRoot>(this WebAssemblyHostBuilder builder, Action<DevHostOptions>? configure = null)
        where TRoot : IComponent
    {
        var root = typeof(TRoot);
        var appId = root.GetCustomAttribute<SubAppRootAttribute>()?.AppId
            ?? throw new InvalidOperationException($"{root.Name} needs @attribute [SubAppRoot(\"your-app-id\")] for the shell to find it.");
        if (SubAppDiscovery.FindRoot([root.Assembly], appId) != root)
            throw new InvalidOperationException($"The shell would open a different component for \"{appId}\": {root.Assembly.GetName().Name} has more than one [SubAppRoot(\"{appId}\")].");

        var options = new DevHostOptions(appId, root);
        configure?.Invoke(options);
        if (options.Users.Count == 0)
            throw new InvalidOperationException("The dev host needs at least one user in Users.");

        builder.Services.AddSingleton(options);
        builder.RootComponents.Add<DevHost>("#app");
        builder.RootComponents.Add<HeadOutlet>("head::after");
        return builder;
    }
}
