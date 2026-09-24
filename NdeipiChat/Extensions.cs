// NdeipiChat/ServiceDiscoveryExtensions.cs
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System;
using System.Net.Http;
using Microsoft.Extensions.Http;

namespace Microsoft.Extensions.DependencyInjection
{
    // Minimal no-op placeholders so calls like services.AddServiceDiscovery()
    // and http.AddServiceDiscovery() compile. Replace with the real implementation
    // or remove these when you add the proper package.
    public static class ServiceDiscoveryExtensions
    {
        public static IServiceCollection AddServiceDiscovery(this IServiceCollection services)
        {
            // TODO: replace with real service discovery registration (NuGet package & using)
            return services;
        }

        public static IHttpClientBuilder AddServiceDiscovery(this IHttpClientBuilder builder)
        {
            // TODO: integrate service discovery into outgoing HTTP clients
            return builder;
        }
    }
}