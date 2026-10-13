using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Hosting;
using Ndeipi.Payments.Api;
using Ndeipi.Payments.Tests.Infrastructure;
using Ndeipi.Payments.Users;

namespace Ndeipi.Payments.Tests;

/// <summary>A small rate limit, to see 429 and Retry-After (SRV-OPS-06).</summary>
public sealed class RateLimitedApp : PaymentsApp
{
    protected override IDictionary<string, string?> Overrides => new Dictionary<string, string?>
    {
        ["Payments:RateLimit:PermitLimit"] = "3",
        ["Payments:RateLimit:Window"] = "00:01:00"
    };
}

/// <summary>
/// A production deployment. No real KYC provider exists yet, so the test supplies one; the fiat
/// rails are PayPal and Absa, which refuse every call until M4, and no exchange is configured.
/// </summary>
public sealed class ProductionApp : PaymentsApp
{
    protected override IDictionary<string, string?> Overrides => new Dictionary<string, string?>
    {
        ["Payments:Environment"] = "production",
        ["Payments:FiatRails:0"] = "paypal",
        ["Payments:FiatRails:1"] = "absa",
        ["Payments:Exchange"] = "none",
        ["Payments:KycProvider"] = "test"
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton<IKycProvider>(sp => sp.GetRequiredService<SimulatedKycProvider>())));
    }
}

public sealed class RateLimitTests(RateLimitedApp app) : IClassFixture<RateLimitedApp>
{
    [Fact]
    public async Task A_key_over_its_limit_gets_429_with_retry_after_and_other_keys_are_unaffected()
    {
        var acme = await app.CreateIntegratorAsync("Acme");
        var zed = await app.CreateIntegratorAsync("Zed");

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.OK, (await acme.GetAsync("/v1/users")).StatusCode);
        var limited = await acme.GetAsync("/v1/users");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("rate_limited", await limited.ErrorCodeAsync());
        Assert.True(int.Parse(limited.Headers.GetValues("Retry-After").Single()) >= 1);
        Assert.Equal(HttpStatusCode.OK, (await zed.GetAsync("/v1/users")).StatusCode);
    }
}

public sealed class ProductionTests(ProductionApp app) : IClassFixture<ProductionApp>
{
    [Fact]
    public async Task Production_issues_live_keys_and_refuses_sandbox_keys()
    {
        var acme = await app.CreateIntegratorAsync(environment: PaymentsEnvironment.Production);
        var sandboxClient = app.CreateClient();
        sandboxClient.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, ApiKeyService.SandboxPrefix + "anything");

        Assert.StartsWith(ApiKeyService.ProductionPrefix, acme.Key);
        Assert.Equal(HttpStatusCode.OK, (await acme.GetAsync("/v1/users")).StatusCode);
        Assert.Equal("key_environment_mismatch", await (await sandboxClient.GetAsync("/v1/users")).ErrorCodeAsync());
    }

    [Fact]
    public async Task Sandbox_calls_answer_sandbox_only_in_production()
    {
        var acme = await app.CreateIntegratorAsync(environment: PaymentsEnvironment.Production);

        var response = await acme.PostAsync("/v1/sandbox/deposits", new { amount = "100.00" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("sandbox_only", await response.ErrorCodeAsync());
    }
}

public sealed class StartupGuardTests
{
    sealed class SimulatedProductionApp : PaymentsApp
    {
        protected override IDictionary<string, string?> Overrides => new Dictionary<string, string?> { ["Payments:Environment"] = "production" };
    }

    [Fact]
    public async Task Production_refuses_to_start_on_simulated_providers()
    {
        await using var app = new SimulatedProductionApp();

        var thrown = Assert.ThrowsAny<Exception>(() => app.CreateClient());
        Assert.Contains("SC-06", thrown.ToString());
    }
}
