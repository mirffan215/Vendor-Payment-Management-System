using System.Net;
using System.Net.Sockets;
using Vpms.BuildingBlocks.Net;

namespace Vpms.UnitTests;

public sealed class OutboundGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]      // cloud metadata
    [InlineData("100.64.0.1")]           // carrier-grade NAT
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("198.18.0.1")]
    [InlineData("192.0.2.5")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:127.0.0.1")]     // IPv4 inside IPv6
    [InlineData("::ffff:10.0.0.5")]
    [InlineData("64:ff9b::a00:1")]       // NAT64 of 10.0.0.1
    [InlineData("2002:a00:1::")]         // 6to4 of 10.0.0.1
    [InlineData("2001:db8::1")]
    [InlineData("2130706433")]           // 127.0.0.1 written as one number
    public void Internal_addresses_are_recognised(string text)
    {
        Assert.True(AddressRules.IsInternalHost(text), text);
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("52.96.0.1")]
    [InlineData("172.15.0.1")]
    [InlineData("172.32.0.1")]
    [InlineData("100.63.255.255")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("contoso.operations.dynamics.com")]
    public void Public_addresses_and_names_are_not_internal(string text)
    {
        Assert.False(AddressRules.IsInternalHost(text), text);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("app.localhost")]
    [InlineData("printer.local")]
    [InlineData("db.internal")]
    public void Internal_names_are_recognised(string name) => Assert.True(AddressRules.IsInternalHost(name));

    [Fact]
    public async Task A_name_that_resolves_to_a_private_address_is_refused()
    {
        // The name looks public but points inside (DNS rebinding, or simply a private zone).
        var ex = await Assert.ThrowsAsync<OutboundBlockedException>(() =>
            OutboundHttp.ConnectSocketAsync("hooks.example.com", 443, false, CancellationToken.None, (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.0.0.7") })));
        Assert.Contains("not allowed", ex.Message);
    }

    [Fact]
    public async Task A_name_with_one_private_answer_among_public_ones_is_refused()
    {
        await Assert.ThrowsAsync<OutboundBlockedException>(() =>
            OutboundHttp.ConnectSocketAsync("hooks.example.com", 443, false, CancellationToken.None,
                (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("::ffff:192.168.0.2") })));
    }

    [Fact]
    public async Task Literal_internal_addresses_and_localhost_are_refused_without_a_lookup()
    {
        foreach (var host in new[] { "127.0.0.1", "[::1]", "localhost", "169.254.169.254" })
        {
            await Assert.ThrowsAsync<OutboundBlockedException>(() => OutboundHttp.ConnectSocketAsync(host, 80, false, CancellationToken.None, (_, _) => throw new InvalidOperationException("must not resolve")));
        }
    }

    [Fact]
    public async Task Internal_destinations_work_when_explicitly_allowed_for_development()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var socket = await OutboundHttp.ConnectSocketAsync("127.0.0.1", port, true, CancellationToken.None);
        Assert.True(socket.Connected);
    }

    [Fact]
    public async Task The_guarded_http_client_refuses_an_internal_destination_and_never_follows_redirects()
    {
        using var client = new HttpClient(OutboundHttp.CreateHandler(allowInternal: false));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://127.0.0.1:1/"));
        using var handler = OutboundHttp.CreateHandler(false);
        Assert.False(handler.AllowAutoRedirect);
    }
}
