using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Ndeipi.Payments.Tests.Infrastructure;
using Ndeipi.Payments.Transfers;

namespace Ndeipi.Payments.Tests;

/// <summary>
/// The server's routes and openapi.yaml must not drift (DC-01, SC-01): every operation in the
/// contract is mapped, and nothing is mapped that the contract does not describe.
/// </summary>
public sealed partial class ContractTests(PaymentsApp app) : IClassFixture<PaymentsApp>
{
    static string ContractPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "payments-api", "openapi.yaml");
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException("docs/payments-api/openapi.yaml not found above the test output folder.");
    }

    /// <summary>"POST /users/{user_id}/wallets" for every operation under <c>paths:</c>.</summary>
    static SortedSet<string> ContractOperations()
    {
        var operations = new SortedSet<string>(StringComparer.Ordinal);
        string? path = null;
        var inPaths = false;
        foreach (var line in File.ReadLines(ContractPath()))
        {
            if (Regex.IsMatch(line, @"^\S"))
                inPaths = line.StartsWith("paths:", StringComparison.Ordinal);
            if (!inPaths)
                continue;
            if (PathLine().Match(line) is { Success: true } p)
                path = p.Groups[1].Value;
            else if (path is not null && MethodLine().Match(line) is { Success: true } m)
                operations.Add($"{m.Groups[1].Value.ToUpperInvariant()} {path}");
        }
        return operations;
    }

    SortedSet<string> ServerOperations()
    {
        var operations = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var pattern = "/" + endpoint.RoutePattern.RawText!.TrimStart('/');
            if (!pattern.StartsWith("/v1/", StringComparison.Ordinal))
                continue;
            foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                operations.Add($"{method} {pattern["/v1".Length..]}");
        }
        return operations;
    }

    [Fact]
    public void Every_contract_operation_is_mapped_and_nothing_else_is()
    {
        var contract = ContractOperations();
        var server = ServerOperations();

        Assert.True(contract.Count >= 39, $"Parsed only {contract.Count} operations from openapi.yaml.");
        Assert.Empty(contract.Except(server));
        Assert.Empty(server.Except(contract));
    }

    [Fact]
    public void Every_transfer_state_in_the_contract_is_one_the_server_knows()
    {
        var yaml = File.ReadAllText(ContractPath());
        var states = Regex.Match(yaml, @"TransferState:[\s\S]*?enum: \[([^\]]+)\]").Groups[1].Value
            .Split(',', StringSplitOptions.TrimEntries);
        var server = Enum.GetNames<TransferState>().Select(n => Regex.Replace(n, "(?<!^)([A-Z])", "_$1").ToLowerInvariant());

        Assert.Equal(states.Order(), server.Order());
    }

    [GeneratedRegex(@"^  (/[^:\s]*):\s*$")]
    private static partial Regex PathLine();

    [GeneratedRegex(@"^    (get|post|put|patch|delete):")]
    private static partial Regex MethodLine();
}
