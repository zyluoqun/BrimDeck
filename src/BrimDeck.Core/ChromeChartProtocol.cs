using System.Buffers.Binary;
using System.Text.Json;

namespace BrimDeck.Core;

// Chrome Native Messaging framing, also used on the current-user-only local pipe.
public static class ChromeChartProtocol
{
    public const string ExtensionId = "klighcgglcfkihjcbdcapjijfhebokff";
    public const string HostName = "com.brimdeck.chart";
    private const int MaxBytes = 65536;
    public static async Task<JsonElement?> ReadAsync(Stream stream, CancellationToken cancellation)
    {
        var header = new byte[4];
        int first = await stream.ReadAsync(header.AsMemory(0, 1), cancellation).ConfigureAwait(false);
        if (first == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), cancellation).ConfigureAwait(false);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (size == 0 || size > MaxBytes) throw new InvalidDataException("Invalid Chrome bridge message length.");
        var bytes = new byte[(int)size];
        await stream.ReadExactlyAsync(bytes, cancellation).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.Clone();
    }
    public static async Task WriteAsync(Stream stream, object message, CancellationToken cancellation)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Chrome bridge message is too large.");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancellation).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellation).ConfigureAwait(false);
        await stream.FlushAsync(cancellation).ConfigureAwait(false);
    }
}
