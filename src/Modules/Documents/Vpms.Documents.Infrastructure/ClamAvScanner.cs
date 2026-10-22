using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Vpms.Documents.Application;

namespace Vpms.Documents.Infrastructure;

/// <summary>Scans through a clamd daemon with the INSTREAM protocol. Any protocol or network problem is an Error, never Clean.</summary>
public sealed class ClamAvScanner(ClamAvOptions options) : IVirusScanner
{
    public async Task<ScanResult> ScanAsync(Stream content, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

        using var client = new TcpClient();
        await client.ConnectAsync(options.Host, options.Port, timeout.Token);
        await using var stream = client.GetStream();

        await stream.WriteAsync("zINSTREAM\0"u8.ToArray(), timeout.Token);
        var buffer = new byte[16 * 1024];
        var length = new byte[4];
        int read;
        while ((read = await content.ReadAsync(buffer, timeout.Token)) > 0)
        {
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)read);
            await stream.WriteAsync(length, timeout.Token);
            await stream.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
        }

        BinaryPrimitives.WriteUInt32BigEndian(length, 0);
        await stream.WriteAsync(length, timeout.Token);
        await stream.FlushAsync(timeout.Token);

        var response = new StringBuilder();
        var one = new byte[1];
        while (await stream.ReadAsync(one, timeout.Token) == 1 && one[0] != 0)
        {
            response.Append((char)one[0]);
            if (response.Length > 512)
            {
                break;
            }
        }

        return Parse(response.ToString().Trim());
    }

    /// <summary>"stream: OK", "stream: Win.Test.EICAR_HDB-1 FOUND" or "... ERROR".</summary>
    public static ScanResult Parse(string response)
    {
        if (response.EndsWith(" OK", StringComparison.Ordinal))
        {
            return new ScanResult(ScanVerdict.Clean);
        }

        if (response.EndsWith(" FOUND", StringComparison.Ordinal))
        {
            var signature = response["stream:".Length..^" FOUND".Length].Trim();
            return new ScanResult(ScanVerdict.Infected, signature.Length == 0 ? "unknown" : signature);
        }

        return new ScanResult(ScanVerdict.Error, Detail: "unexpected scanner response");
    }
}

/// <summary>Development and test scanner: flags only the standard EICAR test string.</summary>
public sealed class EicarVirusScanner : IVirusScanner
{
    private const string Eicar = "X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*";

    public async Task<ScanResult> ScanAsync(Stream content, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        return Encoding.Latin1.GetString(ms.ToArray()).Contains(Eicar, StringComparison.Ordinal)
            ? new ScanResult(ScanVerdict.Infected, "Eicar-Test-Signature")
            : new ScanResult(ScanVerdict.Clean);
    }
}
