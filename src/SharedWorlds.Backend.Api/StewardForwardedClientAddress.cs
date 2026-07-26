using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.HttpOverrides;

namespace SharedWorlds.Backend.Api;

internal static class StewardForwardedClientAddress
{
    internal const string KnownProxyIpConfigurationKey = "ReverseProxy:KnownProxyIp";

    internal static bool Configure(
        IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var configuredProxy = configuration[KnownProxyIpConfigurationKey];
        if (string.IsNullOrWhiteSpace(configuredProxy))
        {
            return false;
        }

        if (!IPAddress.TryParse(configuredProxy.Trim(), out var knownProxyIp))
        {
            throw new InvalidOperationException(
                $"Configuration '{KnownProxyIpConfigurationKey}' must be one exact IP address.");
        }

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            // Host presence needs only the originating client address. Do not expand this trust
            // boundary to forwarded scheme/host/prefix or arbitrary proxy chains.
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
            options.ForwardLimit = 1;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Add(knownProxyIp);

            // Kestrel can expose an IPv4 peer through a dual-mode socket as an IPv4-mapped IPv6
            // address. Trust the equivalent mapped representation without widening the actual host.
            if (knownProxyIp.AddressFamily == AddressFamily.InterNetwork)
            {
                options.KnownProxies.Add(knownProxyIp.MapToIPv6());
            }
        });

        return true;
    }
}
