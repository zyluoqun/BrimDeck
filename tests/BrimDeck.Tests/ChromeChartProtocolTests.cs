using BrimDeck.Core;
using System.Text.Json;

internal static class ChromeChartProtocolTests
{
    internal static async Task Run(Action<string, bool> check)
    {
        using var wire = new MemoryStream();
        await ChromeChartProtocol.WriteAsync(wire, new { id = "one", workspaceId = "layout123" }, default);
        await ChromeChartProtocol.WriteAsync(wire, new { id = "two", title = "四屏 ETHUSDT" }, default);
        wire.Position = 0;
        var first = await ChromeChartProtocol.ReadAsync(wire, default);
        var second = await ChromeChartProtocol.ReadAsync(wire, default);
        check("Chrome bridge framing separates consecutive messages and preserves Unicode", first?.GetProperty("id").GetString() == "one"
            && second?.GetProperty("title").GetString() == "四屏 ETHUSDT");
        check("Closing the Chrome message stream returns clean EOF", await ChromeChartProtocol.ReadAsync(wire, default) is null);
        bool rejected = false;
        using var oversized = new MemoryStream([255, 255, 255, 127]);
        try { await ChromeChartProtocol.ReadAsync(oversized, default); } catch (InvalidDataException) { rejected = true; }
        check("An oversized Chrome frame is rejected before allocating its payload", rejected);
        using var truncated = new MemoryStream([10, 0, 0, 0, 123]); rejected = false;
        try { await ChromeChartProtocol.ReadAsync(truncated, default); } catch (EndOfStreamException) { rejected = true; }
        check("A truncated Chrome frame cannot be treated as a successful response", rejected);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); rejected = false;
        try { await ChromeChartProtocol.WriteAsync(wire, new { id = "cancelled" }, cancelled.Token); } catch (OperationCanceledException) { rejected = true; }
        check("Cancelled Chrome requests are not written", rejected);
    }
}
