using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Vpms.Documents.Application;
using Vpms.Documents.Infrastructure;

namespace Vpms.UnitTests.Documents;

public class ClamAvScannerTests
{
    [Theory]
    [InlineData("stream: OK", ScanVerdict.Clean, null)]
    [InlineData("stream: Win.Test.EICAR_HDB-1 FOUND", ScanVerdict.Infected, "Win.Test.EICAR_HDB-1")]
    [InlineData("INSTREAM size limit exceeded. ERROR", ScanVerdict.Error, null)]
    [InlineData("", ScanVerdict.Error, null)]
    [InlineData("something unexpected", ScanVerdict.Error, null)]
    public void Parses_clamd_responses(string response, ScanVerdict verdict, string? signature)
    {
        var result = ClamAvScanner.Parse(response);
        Assert.Equal(verdict, result.Verdict);
        Assert.Equal(signature, result.Signature);
    }

    [Fact]
    public async Task Speaks_the_instream_protocol_and_returns_the_verdict()
    {
        var payload = new byte[40_000];
        Random.Shared.NextBytes(payload);
        await using var server = FakeClamd.Start("stream: OK\0");

        var result = await new ClamAvScanner(new ClamAvOptions { Host = "127.0.0.1", Port = server.Port, TimeoutSeconds = 5 })
            .ScanAsync(new MemoryStream(payload));

        Assert.Equal(ScanVerdict.Clean, result.Verdict);
        var received = await server.Received;
        Assert.Equal("zINSTREAM\0", received.Command);
        Assert.Equal(payload, received.Data);
        Assert.True(received.Chunks > 1, "large files are sent in several length-prefixed chunks");
        Assert.True(received.Terminated);
    }

    [Fact]
    public async Task Reports_infections_with_the_signature()
    {
        await using var server = FakeClamd.Start("stream: Eicar-Test-Signature FOUND\0");
        var result = await new ClamAvScanner(new ClamAvOptions { Host = "127.0.0.1", Port = server.Port, TimeoutSeconds = 5 })
            .ScanAsync(new MemoryStream("data"u8.ToArray()));
        Assert.Equal(ScanVerdict.Infected, result.Verdict);
        Assert.Equal("Eicar-Test-Signature", result.Signature);
    }

    [Fact]
    public async Task A_scanner_that_hangs_up_is_an_error_never_clean()
    {
        await using var server = FakeClamd.Start("");
        var result = await new ClamAvScanner(new ClamAvOptions { Host = "127.0.0.1", Port = server.Port, TimeoutSeconds = 5 })
            .ScanAsync(new MemoryStream("data"u8.ToArray()));
        Assert.Equal(ScanVerdict.Error, result.Verdict);
    }

    [Fact]
    public async Task An_unreachable_scanner_throws_so_the_attachment_stays_quarantined()
    {
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            new ClamAvScanner(new ClamAvOptions { Host = "127.0.0.1", Port = port, TimeoutSeconds = 2 }).ScanAsync(new MemoryStream([1])));
    }

    [Fact]
    public async Task The_eicar_scanner_flags_only_the_test_string()
    {
        var scanner = new EicarVirusScanner();
        var eicar = "X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*";
        Assert.Equal(ScanVerdict.Infected, (await scanner.ScanAsync(new MemoryStream(Encoding.ASCII.GetBytes("prefix " + eicar)))).Verdict);
        Assert.Equal(ScanVerdict.Clean, (await scanner.ScanAsync(new MemoryStream("harmless"u8.ToArray()))).Verdict);
    }

    private sealed record Received(string Command, byte[] Data, int Chunks, bool Terminated);

    private sealed class FakeClamd : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        public int Port { get; }
        public Task<Received> Received { get; }

        private FakeClamd(TcpListener listener, string reply)
        {
            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Received = Serve(reply);
        }

        public static FakeClamd Start(string reply)
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            return new FakeClamd(l, reply);
        }

        private async Task<Received> Serve(string reply)
        {
            using var client = await _listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var command = new byte[10];
            await stream.ReadExactlyAsync(command);
            var data = new MemoryStream();
            var chunks = 0;
            var terminated = false;
            var len = new byte[4];
            while (true)
            {
                await stream.ReadExactlyAsync(len);
                var n = (int)BinaryPrimitives.ReadUInt32BigEndian(len);
                if (n == 0)
                {
                    terminated = true;
                    break;
                }

                var chunk = new byte[n];
                await stream.ReadExactlyAsync(chunk);
                data.Write(chunk);
                chunks++;
            }

            if (reply.Length > 0)
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes(reply));
            }

            return new Received(Encoding.ASCII.GetString(command), data.ToArray(), chunks, terminated);
        }

        public ValueTask DisposeAsync()
        {
            _listener.Stop();
            return ValueTask.CompletedTask;
        }
    }
}
