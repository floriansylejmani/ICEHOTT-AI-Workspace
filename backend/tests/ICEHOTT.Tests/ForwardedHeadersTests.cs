using System.Net;
using ICEHOTT.API.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ICEHOTT.Tests;

/// <summary>
/// The auth rate limiter partitions by RemoteIpAddress. Behind the platform proxy every
/// caller would share the proxy's address (one global bucket), so forwarded headers must
/// be honoured, but only from explicitly trusted proxy networks.
/// </summary>
public sealed class ForwardedHeadersTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(x => x.Key, x => (string?)x.Value))
            .Build();

    [Fact]
    public void Disabled_By_Default() =>
        Assert.Null(ForwardedHeadersSetup.BuildOptions(Config()));

    [Fact]
    public void Enabled_Without_Trusted_Networks_Is_Rejected_Rather_Than_Trusting_Everyone()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ForwardedHeadersSetup.BuildOptions(Config(("ForwardedHeaders:Enabled", "true"))));
        Assert.Contains("TrustedNetworks", exception.Message);
    }

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    [InlineData("10.0.0.0")]
    [InlineData("10.0.0.0/40")]
    [InlineData("garbage/8")]
    public void Invalid_Or_Universal_Networks_Are_Rejected(string network) =>
        Assert.Throws<InvalidOperationException>(() =>
            ForwardedHeadersSetup.BuildOptions(Config(
                ("ForwardedHeaders:Enabled", "true"),
                ("ForwardedHeaders:TrustedNetworks:0", network))));

    [Fact]
    public void Trusted_Networks_And_Limit_Are_Applied()
    {
        var options = ForwardedHeadersSetup.BuildOptions(Config(
            ("ForwardedHeaders:Enabled", "true"),
            ("ForwardedHeaders:TrustedNetworks:0", "10.0.0.0/8"),
            ("ForwardedHeaders:TrustedNetworks:1", "fd12::/16")))!;

        Assert.Equal(2, options.KnownNetworks.Count);
        Assert.Empty(options.KnownProxies);
        Assert.Equal(1, options.ForwardLimit);
    }

    private static async Task<(string Ip, string Scheme)> CallAsync(
        IPAddress peer, string? forwardedFor, string? forwardedProto)
    {
        var options = ForwardedHeadersSetup.BuildOptions(Config(
            ("ForwardedHeaders:Enabled", "true"),
            ("ForwardedHeaders:TrustedNetworks:0", "10.0.0.0/8")))!;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            // The socket peer, as Kestrel would report it.
            context.Connection.RemoteIpAddress = peer;
            return next();
        });
        app.UseForwardedHeaders(options);
        app.MapGet("/who", (HttpContext context) =>
            $"{context.Connection.RemoteIpAddress}|{context.Request.Scheme}");
        await app.StartAsync();

        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/who");
        if (forwardedFor is not null) request.Headers.Add("X-Forwarded-For", forwardedFor);
        if (forwardedProto is not null) request.Headers.Add("X-Forwarded-Proto", forwardedProto);
        var parts = (await (await client.SendAsync(request)).Content.ReadAsStringAsync()).Split('|');
        return (parts[0], parts[1]);
    }

    [Fact]
    public async Task Trusted_Proxy_Supplies_The_Client_Address_And_Scheme()
    {
        var (ip, scheme) = await CallAsync(IPAddress.Parse("10.1.2.3"), "203.0.113.9", "https");
        Assert.Equal("203.0.113.9", ip);
        Assert.Equal("https", scheme);
    }

    [Fact]
    public async Task Untrusted_Peer_Cannot_Spoof_Its_Address()
    {
        var (ip, scheme) = await CallAsync(IPAddress.Parse("198.51.100.7"), "203.0.113.9", "https");
        Assert.Equal("198.51.100.7", ip);
        Assert.Equal("http", scheme);
    }

    [Fact]
    public async Task Client_Supplied_Forwarded_Chain_Cannot_Override_The_Proxy_Appended_Address()
    {
        // The client sent "1.1.1.1"; the trusted edge appended the real peer. With
        // ForwardLimit=1 only the entry the trusted proxy appended is used.
        var (ip, _) = await CallAsync(IPAddress.Parse("10.1.2.3"), "1.1.1.1, 203.0.113.9", null);
        Assert.Equal("203.0.113.9", ip);
    }
}
