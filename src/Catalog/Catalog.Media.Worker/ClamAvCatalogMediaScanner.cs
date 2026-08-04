using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Aggregator.CatalogMedia.Application;

namespace Aggregator.CatalogMedia.Worker;

public sealed class ClamAvCatalogMediaScanner(CatalogMediaWorkerOptions options) : ICatalogMediaScanner
{
    public async Task<CatalogMediaScanResult> ScanAsync(Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var client = new TcpClient();
        await client.ConnectAsync(options.ClamAvHost, options.ClamAvPort, cancellationToken);
        await using var network = client.GetStream();
        await network.WriteAsync("zINSTREAM\0"u8.ToArray(), cancellationToken);
        var buffer = new byte[64 * 1024];
        var lengthBuffer = new byte[4];
        while (true)
        {
            var read = await content.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            BinaryPrimitives.WriteInt32BigEndian(lengthBuffer, read);
            await network.WriteAsync(lengthBuffer, cancellationToken);
            await network.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        Array.Clear(lengthBuffer);
        await network.WriteAsync(lengthBuffer, cancellationToken);
        await network.FlushAsync(cancellationToken);
        using var response = new MemoryStream();
        while (true)
        {
            var read = await network.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            response.Write(buffer, 0, read);
            if (buffer.AsSpan(0, read).Contains((byte)0)) break;
        }
        var message = Encoding.UTF8.GetString(response.ToArray()).TrimEnd('\0', '\r', '\n');
        if (message.EndsWith("OK", StringComparison.Ordinal)) return new CatalogMediaScanResult(true, null);
        if (message.EndsWith("FOUND", StringComparison.Ordinal))
        {
            var separator = message.LastIndexOf(':');
            var threat = separator >= 0 ? message[(separator + 1)..].Replace("FOUND", "", StringComparison.Ordinal).Trim() : "unknown";
            return new CatalogMediaScanResult(false, threat);
        }
        throw new CatalogMediaApplicationException(
            "CatalogMedia.Scanner",
            "CATALOG_MEDIA_SCANNER_RESPONSE_INVALID",
            503,
            $"ClamAV returned an unsupported response: '{message}'.",
            "Restore the scanner service before retrying media processing.");
    }
}
