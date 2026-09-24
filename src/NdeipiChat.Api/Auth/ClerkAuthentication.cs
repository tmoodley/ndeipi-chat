using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NdeipiChat.Contracts;

namespace Ndeipi.Api.Auth;

public static class ClerkAuthentication
{
    public const string UserIdClaim = "sub";
    public const string SessionIdClaim = "sid";
    public const string AuthorizedPartyClaim = "azp";

    public static IServiceCollection AddClerkAuthentication(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<ClerkOptions>(config.GetSection(ClerkOptions.Section));
        services.Configure<MobileAuthOptions>(config.GetSection(MobileAuthOptions.Section));

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        // Configured from options rather than read from config up front, so settings supplied by
        // the host (user secrets, environment, test overrides) all apply.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<ClerkOptions>>((o, clerkOptions) =>
            {
                var clerk = clerkOptions.Value;
                o.MapInboundClaims = false;
                if (clerk.AuthorityUrl.Length > 0)
                    o.Authority = clerk.AuthorityUrl;

                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = clerk.AuthorityUrl,
                    ValidateAudience = false, // Clerk session tokens carry no aud; azp is checked below.
                    NameClaimType = UserIdClaim,
                    ClockSkew = TimeSpan.FromSeconds(30)
                };

                o.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        // WebSockets can't carry an Authorization header, so SignalR sends the
                        // token in the query string. Accept it there for the hub only.
                        var token = context.Request.Query["access_token"].ToString();
                        if (token.Length > 0 && context.Request.Path.StartsWithSegments(ChatHubContract.Path))
                            context.Token = token;
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = context =>
                    {
                        var parties = clerk.AuthorizedParties;
                        var azp = context.Principal?.FindFirst(AuthorizedPartyClaim)?.Value;
                        if (azp is not null && parties.Length > 0 && !parties.Contains(azp, StringComparer.OrdinalIgnoreCase))
                            context.Fail("The token was issued to an origin this API doesn't trust.");
                        return Task.CompletedTask;
                    }
                };
            });

        services.AddAuthorization();

        services.AddHttpClient<IClerkBackendApi, ClerkBackendApi>((sp, http) =>
        {
            var clerk = sp.GetRequiredService<IOptions<ClerkOptions>>().Value;
            http.BaseAddress = new Uri(clerk.BackendApiUrl.TrimEnd('/') + "/");
            if (clerk.SecretKey.Length > 0)
                http.DefaultRequestHeaders.Authorization = new("Bearer", clerk.SecretKey);
        });

        services.AddScoped<CurrentUserService>();
        services.AddScoped<MobileAuthService>();
        services.AddSingleton<IUserIdProvider, ClerkUserIdProvider>();
        return services;
    }
}

/// <summary>SignalR addresses users by their Clerk id, so <c>Clients.User(...)</c> reaches every device they're signed in on.</summary>
public sealed class ClerkUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) =>
        connection.User?.FindFirst(ClerkAuthentication.UserIdClaim)?.Value;
}
