using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;

namespace ICEHOTT.API.Hosting;

/// <summary>
/// Honours X-Forwarded-For / X-Forwarded-Proto from the managed reverse proxy, and only
/// from explicitly configured proxy networks. Behind the platform proxy the socket peer is
/// always the proxy, so without this every caller shares one address and the per-IP auth
/// rate limiter becomes a single global bucket. Trusting every peer would instead let any
/// client spoof its address, so an enabled setting with no trusted network is rejected.
/// </summary>
public static class ForwardedHeadersSetup
{
    public const string Section = "ForwardedHeaders";

    public static bool TryParseNetwork(string? value, out Microsoft.AspNetCore.HttpOverrides.IPNetwork network)
    {
        network = default!;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var parts = value.Trim().Split('/');
        if (parts.Length != 2 ||
            !IPAddress.TryParse(parts[0], out var address) ||
            !int.TryParse(parts[1], out var prefix))
            return false;

        var maximum = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        // A /0 network would trust every peer, which defeats the purpose.
        if (prefix < 1 || prefix > maximum)
            return false;

        network = new Microsoft.AspNetCore.HttpOverrides.IPNetwork(address, prefix);
        return true;
    }

    public static bool IsEnabled(IConfiguration configuration) =>
        bool.TryParse(configuration[$"{Section}:Enabled"], out var enabled) && enabled;

    /// <summary>Returns null when forwarding is disabled; throws when it is enabled unsafely.</summary>
    public static ForwardedHeadersOptions? BuildOptions(IConfiguration configuration)
    {
        if (!IsEnabled(configuration))
            return null;

        var networks = configuration.GetSection($"{Section}:TrustedNetworks").GetChildren()
            .Select(x => x.Value).ToArray();
        if (networks.Length == 0)
            throw new InvalidOperationException(
                $"{Section}:TrustedNetworks must list the proxy network(s) when {Section}:Enabled is true.");

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = Math.Clamp(
                configuration.GetValue<int?>($"{Section}:ForwardLimit") ?? 1, 1, 5),
            RequireHeaderSymmetry = false
        };

        // Replace the loopback-only defaults with exactly the configured networks.
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var value in networks)
        {
            if (!TryParseNetwork(value, out var network))
                throw new InvalidOperationException(
                    $"{Section}:TrustedNetworks contains an invalid or unbounded network; use CIDR notation with a prefix of at least /1.");
            options.KnownNetworks.Add(network);
        }

        return options;
    }
}
