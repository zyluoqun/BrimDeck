using System.Net;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BrimDeck.Core;

internal static class OpenMarketChartTests
{
    public static async Task Run(Action<string, bool> check)
    {
        using var handler = new ChartHandler();
        using var http = new HttpClient(handler);
        var client = new OpenMarketChartClient(http, "test-token", "layout123");
        check("Workspace URLs are normalized without relying on the browser's active tab", OpenMarketChartClient.ParseWorkspace("https://openmarket.xyz/chart/layout123") == "layout123");
        bool badWorkspace = false;
        try { _ = OpenMarketChartClient.ParseWorkspace("https://example.com/chart/layout123"); } catch (ArgumentException) { badWorkspace = true; }
        check("Chart connections reject unrelated websites", badWorkspace);
        var coin = new MarketCoin("BTC", "BTCUSDT", true, 1, 1);
        await CheckPinnedPanes(coin, check);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => client.SwitchAsync(coin, CancellationToken.None)));
        check("Chart clicks rotate only through the three lower panes, including concurrent clicks", results.Select(r => r.ChartIndex).SequenceEqual([1, 2, 3, 1]));
        check("Chart clicks use 4h and never change pane zero", handler.Writes.All(w => w.GetProperty("chartIndex").GetInt32() is >= 1 and <= 3)
            && handler.Intervals.SequenceEqual(Enumerable.Repeat("4h", 4)));
        check("Every chart read and write names the configured workspace", handler.Targets.All(id => id == "layout123"));
        check("Chart transport uses the scoped token", handler.Authorization == "Bearer test-token");
        check("Futures retain their exact market symbol and exchange", handler.Symbols.All(w => w.GetProperty("symbol").GetString() == "BTCUSDT" && w.GetProperty("exchange").GetString() == "BINANCE_FUTURES"));
        handler.FailSymbol = true;
        bool failed = false;
        try { await client.SwitchAsync(coin, CancellationToken.None); } catch (InvalidOperationException) { failed = true; }
        handler.FailSymbol = false;
        var retry = await client.SwitchAsync(coin with { Perpetual = false }, CancellationToken.None);
        check("A refused chart change retains its pane for the next click", failed && retry.ChartIndex == 2);
        check("Spot charts use the spot venue", handler.Symbols.Last().GetProperty("exchange").GetString() == "BINANCE");
        handler.Sync = true; int writes = handler.Writes.Count;
        failed = false;
        try { await client.SwitchAsync(coin, CancellationToken.None); } catch (InvalidOperationException) { failed = true; }
        check("Synchronization that could change the top pane is refused before any write", failed && handler.Writes.Count == writes);
        handler.Sync = false; handler.Open = false;
        failed = false;
        try { await client.SwitchAsync(coin, CancellationToken.None); } catch (InvalidOperationException) { failed = true; }
        check("An absent chart tab does not queue a hidden change or open a new tab", failed && handler.Writes.Count == writes);
        handler.Open = true; handler.ValidLayout = false;
        failed = false;
        try { await client.SwitchAsync(coin, CancellationToken.None); } catch (InvalidOperationException) { failed = true; }
        check("A changed layout is refused instead of writing into another screen", failed && handler.Writes.Count == writes);
        handler.ValidLayout = true; handler.Loaded = false; handler.OwnerAcknowledges = false; failed = false;
        try { await client.SwitchAsync(coin, CancellationToken.None); } catch (InvalidOperationException error) { failed = error.Message.Contains("刷新") || error.Message.Contains("Refresh"); }
        check("A REST save without a live browser acknowledgement requests a page reconnect", failed);
        handler.OwnerAcknowledges = true; handler.OwnerMarketMismatch = true; failed = false;
        try { await client.SwitchAsync(coin, CancellationToken.None); } catch (InvalidOperationException) { failed = true; }
        check("An old matching status cannot hide a different market returned by the browser itself", failed);
        handler.OwnerMarketMismatch = false;
        handler.OwnerAcknowledges = true; handler.Loaded = true;
        check("An unexecuted browser change does not advance the next target pane", (await client.SwitchAsync(coin, CancellationToken.None)).ChartIndex == 3);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); writes = handler.Writes.Count;
        try { await client.SwitchAsync(coin, cancelled.Token); } catch (OperationCanceledException) { }
        check("Cancelled clicks never mutate a chart", handler.Writes.Count == writes);
        handler.Unauthorized = true; failed = false;
        try { await client.SwitchAsync(coin, CancellationToken.None); } catch (InvalidOperationException error) { failed = !error.Message.Contains("test-token"); }
        check("Expired credentials stop writes and are not exposed by the error", failed && handler.Writes.Count == writes);
        using var recoveryHandler = new ChartHandler { TemporaryReadFailures = 1 };
        using var recoveryHttp = new HttpClient(recoveryHandler);
        var recovery = new OpenMarketChartClient(recoveryHttp, "test-token", "layout123");
        var recovered = await recovery.SwitchAsync(coin, CancellationToken.None);
        check("A temporary 503 on a read is retried without duplicating a chart write", recovered.ChartIndex == 1 && recoveryHandler.Symbols.Count == 1);
        recoveryHandler.ServiceDown = true; failed = false;
        try { await recovery.SwitchAsync(coin, CancellationToken.None); } catch (InvalidOperationException error) { failed = error.Message.Contains("503"); }
        check("A service outage names HTTP 503 instead of claiming a generic network timeout", failed && recoveryHandler.Symbols.Count == 1);
        recoveryHandler.ServiceDown = false;
        check("Service recovery keeps the pane that was pending during the outage", (await recovery.SwitchAsync(coin, CancellationToken.None)).ChartIndex == 2);
        using var streamHandler = new ChartHandler { PersistentStream = true };
        using var streamHttp = new HttpClient(streamHandler);
        var streaming = new OpenMarketChartClient(streamHttp, "test-token", "layout123");
        using var streamDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        bool streamCompleted = false;
        try { streamCompleted = (await streaming.SwitchAsync(coin, streamDeadline.Token)).ChartIndex == 1; } catch (OperationCanceledException) { }
        check("SSE replies complete without waiting for a persistent stream to close", streamCompleted);
        using var uncertainHandler = new ChartHandler { DropSymbolReply = true };
        using var uncertainHttp = new HttpClient(uncertainHandler);
        var uncertain = new OpenMarketChartClient(uncertainHttp, "test-token", "layout123");
        var confirmed = await uncertain.SwitchAsync(coin, CancellationToken.None);
        check("A lost mutation reply is reconciled from the target pane without repeating the write", confirmed.ChartIndex == 1 && uncertainHandler.Symbols.Count == 1);
        uncertainHandler.DropSymbolReply = false;
        check("A reconciled mutation advances the rotation exactly once", (await uncertain.SwitchAsync(coin, CancellationToken.None)).ChartIndex == 2);

        using var idleHandler = new ChartHandler();
        using var idleHttp = new HttpClient(idleHandler);
        var idle = new OpenMarketChartClient(idleHttp, "test-token", "layout123");
        await idle.SwitchAsync(coin, CancellationToken.None);
        idleHandler.StaleStatusReads = 2;
        ChartSwitchResult[] afterIdle = [];
        try { afterIdle = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => idle.SwitchAsync(coin, CancellationToken.None))); }
        catch (InvalidOperationException) { }
        check("An idle tab can finish state sync before queued clicks rotate exactly once each", afterIdle.Select(r => r.ChartIndex).SequenceEqual([2, 3])
            && idleHandler.Symbols.Count == 3 && !idleHandler.WroteWhileStale);

        using var delayedHandler = new ChartHandler { Loaded = false, PendingReadResponses = 2 };
        using var delayedHttp = new HttpClient(delayedHandler);
        var delayed = new OpenMarketChartClient(delayedHttp, "test-token", "layout123");
        ChartSwitchResult? delayedResult = null;
        try { delayedResult = await delayed.SwitchAsync(coin, CancellationToken.None); } catch (InvalidOperationException) { }
        check("A blocked read and an old browser market are polled until the requested market is confirmed", delayedResult?.ChartIndex == 1
            && delayedHandler.ReadCount == 3 && delayedHandler.Symbols.Count == 1);
        check("Delayed confirmation advances the rotation only once", (await delayed.SwitchAsync(coin, CancellationToken.None)).ChartIndex == 2);

        using var delayedReplyHandler = new ChartHandler { DropSymbolReply = true, PendingReadResponses = 2 };
        using var delayedReplyHttp = new HttpClient(delayedReplyHandler);
        var delayedReply = new OpenMarketChartClient(delayedReplyHttp, "test-token", "layout123");
        check("A lost reply also allows delayed browser confirmation without resending the mutation",
            (await delayedReply.SwitchAsync(coin, CancellationToken.None)).ChartIndex == 1
            && delayedReplyHandler.ReadCount == 3 && delayedReplyHandler.Symbols.Count == 1);

        using var reconnectHandler = new ChartHandler { DisconnectedStatusReads = 2 };
        using var reconnectHttp = new HttpClient(reconnectHandler);
        var reconnect = new OpenMarketChartClient(reconnectHttp, "test-token", "layout123");
        check("A temporarily disconnected tab can reconnect before any mutation", (await reconnect.SwitchAsync(coin, CancellationToken.None)).ChartIndex == 1
            && reconnectHandler.Symbols.Count == 1 && !reconnectHandler.WroteWhileStale);

        using var cancelRecovery = new CancellationTokenSource();
        using var cancelHandler = new ChartHandler { Open = false, OnCall = name => { if (name == "chart_status") cancelRecovery.Cancel(); } };
        using var cancelHttp = new HttpClient(cancelHandler);
        var cancelClient = new OpenMarketChartClient(cancelHttp, "test-token", "layout123");
        bool recoveryCancelled = false;
        try { await cancelClient.SwitchAsync(coin, cancelRecovery.Token); } catch (OperationCanceledException) { recoveryCancelled = true; }
        check("Cancellation during connection recovery stops waiting without writing", recoveryCancelled && cancelHandler.Writes.Count == 0);
        cancelHandler.Open = true; cancelHandler.OnCall = null;
        check("Cancelled recovery releases the queue and preserves the target pane", (await cancelClient.SwitchAsync(coin, CancellationToken.None)).ChartIndex == 1);

        using var cancelConfirmation = new CancellationTokenSource();
        using var cancelReadHandler = new ChartHandler { Loaded = false, OwnerAcknowledges = false,
            OnCall = name => { if (name == "chart_read") cancelConfirmation.Cancel(); } };
        using var cancelReadHttp = new HttpClient(cancelReadHandler);
        var cancelReadClient = new OpenMarketChartClient(cancelReadHttp, "test-token", "layout123");
        bool confirmationCancelled = false;
        try { await cancelReadClient.SwitchAsync(coin, cancelConfirmation.Token); } catch (OperationCanceledException) { confirmationCancelled = true; }
        check("Cancellation during browser confirmation does not resend the mutation", confirmationCancelled && cancelReadHandler.Symbols.Count == 1);
        cancelReadHandler.OnCall = null; cancelReadHandler.Loaded = true;
        check("Cancelled confirmation releases the queue without advancing the rotation", (await cancelReadClient.SwitchAsync(coin, CancellationToken.None)).ChartIndex == 1);

        using var hungReadHandler = new ChartHandler { Loaded = false, HangRead = true };
        using var hungReadHttp = new HttpClient(hungReadHandler);
        var hungReadClient = new OpenMarketChartClient(hungReadHttp, "test-token", "layout123");
        var elapsed = Stopwatch.StartNew(); failed = false;
        try { await hungReadClient.SwitchAsync(coin, CancellationToken.None); } catch (InvalidOperationException) { failed = true; }
        check("An unresponsive browser read is stopped by the recovery deadline without resending the mutation", failed
            && elapsed.Elapsed < TimeSpan.FromSeconds(15) && hungReadHandler.Symbols.Count == 1);
        hungReadHandler.HangRead = false; hungReadHandler.Loaded = true;
        check("A confirmation deadline leaves the same pane available for retry", (await hungReadClient.SwitchAsync(coin, CancellationToken.None)).ChartIndex == 1);

        using var refusalHandler = new ChartHandler { FailSymbol = true, SymbolErrorCode = "invalid_symbol", SymbolErrorMessage = "Unsupported symbol 牛来USDT test-token" };
        using var refusalHttp = new HttpClient(refusalHandler);
        var refusal = new OpenMarketChartClient(refusalHttp, "test-token", "layout123");
        string refusalMessage = "";
        try { await refusal.SwitchAsync(coin, CancellationToken.None); } catch (InvalidOperationException ex) { refusalMessage = ex.Message; }
        check("Chart refusals expose the server reason without exposing the token", refusalMessage.Contains("invalid_symbol") && refusalMessage.Contains("牛来USDT") && !refusalMessage.Contains("test-token"));
        refusalHandler.SymbolErrorCode = "VALIDATION"; refusalHandler.SymbolErrorMessage = "symbol must be alphanumeric (1-64 chars, allowed punctuation: . _ : | / -)";
        refusalMessage = "";
        try { await refusal.SwitchAsync(new("牛来", "牛来USDT", true, 1, 1), CancellationToken.None); } catch (InvalidOperationException ex) { refusalMessage = ex.Message; }
        check("Unicode symbol validation names the provider limitation instead of blaming credentials", refusalMessage.Contains("中文交易对") || refusalMessage.Contains("non-English symbols"));
        refusalHandler.FailSymbol = false;
        check("A provider Unicode rejection keeps the pending chart pane", (await refusal.SwitchAsync(coin, CancellationToken.None)).ChartIndex == 1);
    }

    private static async Task CheckPinnedPanes(MarketCoin coin, Action<string, bool> check)
    {
        int[][] choices = [[], [1], [2], [3], [1, 2], [1, 3], [2, 3]];
        foreach (var pinned in choices)
        {
            using var handler = new ChartHandler();
            using var http = new HttpClient(handler);
            var client = new OpenMarketChartClient(http, "test-token", "layout123");
            var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => client.SwitchAsync(coin, CancellationToken.None, pinned)));
            var available = Enumerable.Range(1, 3).Except(pinned).ToArray();
            check($"Pinned panes [{string.Join(',', pinned)}] are skipped in the shared concurrent rotation",
                results.Select(r => r.ChartIndex).SequenceEqual(Enumerable.Range(0, 6).Select(i => available[i % available.Length])));
            check($"Pinned panes [{string.Join(',', pinned)}] receive neither interval nor symbol writes",
                handler.Writes.All(w => available.Contains(w.GetProperty("chartIndex").GetInt32())) && handler.Symbols.Count == 6);
        }
        using var changingHandler = new ChartHandler();
        using var changingHttp = new HttpClient(changingHandler);
        var changing = new OpenMarketChartClient(changingHttp, "test-token", "layout123");
        await changing.SwitchAsync(coin, CancellationToken.None);
        check("Pinning the pending pane skips it without resetting the rotation", (await changing.SwitchAsync(coin, CancellationToken.None, [2])).ChartIndex == 3);
        check("Unpinning restores a pane in its original order", (await changing.SwitchAsync(coin, CancellationToken.None)).ChartIndex == 1
            && (await changing.SwitchAsync(coin, CancellationToken.None)).ChartIndex == 2);
        changingHandler.FailSymbol = true;
        bool failed = false;
        try { await changing.SwitchAsync(coin, CancellationToken.None, [3]); } catch (InvalidOperationException) { failed = true; }
        changingHandler.FailSymbol = false;
        check("A failed switch with a pinned pending pane retries the same available pane", failed
            && (await changing.SwitchAsync(coin, CancellationToken.None, [3])).ChartIndex == 1);
        int writes = changingHandler.Writes.Count;
        bool rejected = false;
        try { await changing.SwitchAsync(coin, CancellationToken.None, [1, 2, 3]); } catch (ArgumentException) { rejected = true; }
        check("Pinning all three panes is rejected before any chart write", rejected && changingHandler.Writes.Count == writes);
        changingHandler.DropSymbolReply = true;
        check("A recovered mutation acknowledgement also skips pinned panes", (await changing.SwitchAsync(coin, CancellationToken.None, [2, 3])).ChartIndex == 1
            && (await changing.SwitchAsync(coin, CancellationToken.None, [2, 3])).ChartIndex == 1);
    }

    private sealed class ChartHandler : HttpMessageHandler
    {
        public bool FailSymbol, Sync, Unauthorized, Loaded = true, Open = true, ValidLayout = true;
        public bool ServiceDown, PersistentStream, OwnerMarketMismatch, OwnerAcknowledges = true;
        public bool DropSymbolReply, HangRead;
        public string? SymbolErrorCode, SymbolErrorMessage;
        public int StaleStatusReads, DisconnectedStatusReads, PendingReadResponses, ReadCount;
        public bool WroteWhileStale;
        public Action<string>? OnCall;
        private bool _lastStatusReady = true;
        private readonly Dictionary<int, (string Symbol, string Exchange)> _applied = [];
        public int TemporaryReadFailures;
        public string? Authorization;
        public List<JsonElement> Writes = [], Symbols = [];
        public List<string?> Intervals = [], Targets = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Yield();
            if (Unauthorized) return new(HttpStatusCode.Unauthorized);
            Authorization = request.Headers.Authorization?.ToString();
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = body.RootElement;
            if (ServiceDown || root.GetProperty("method").GetString() == "tools/call"
                && root.GetProperty("params").GetProperty("name").GetString() == "chart_status" && TemporaryReadFailures-- > 0)
                return new(HttpStatusCode.ServiceUnavailable);
            object response;
            if (root.GetProperty("method").GetString() == "initialize")
                response = new { jsonrpc = "2.0", id = root.GetProperty("id").GetInt32(), result = new { protocolVersion = "2025-03-26" } };
            else
            {
                var p = root.GetProperty("params"); string name = p.GetProperty("name").GetString()!; var args = p.GetProperty("arguments");
                OnCall?.Invoke(name);
                if (name == "chart_read" && HangRead) await Task.Delay(Timeout.Infinite, cancellationToken);
                bool stale = name == "chart_status" && StaleStatusReads-- > 0;
                bool disconnected = name == "chart_status" && DisconnectedStatusReads-- > 0;
                if (name == "chart_status") _lastStatusReady = Open && !stale && !disconnected;
                bool pendingRead = name == "chart_read" && PendingReadResponses-- > 0;
                if (name == "chart_read") ReadCount++;
                if (args.TryGetProperty("workspaceId", out var id)) Targets.Add(id.GetString());
                object data = name switch
                {
                    "chart_workspace_select" => new { workspaceId = "layout123", switched = true },
                    "chart_status" => new { workspaces = new[] { new { workspaceId = "layout123", state = "open", humanPresent = Open && !disconnected, hasStateSync = true, stateStale = stale,
                        panes = OwnerAcknowledges ? _applied.Select(p => new { chartIndex = p.Key, transformations = p.Value.Exchange + "|" + p.Value.Symbol, loadState = "loaded" }).ToArray() : [] } } },
                    "chart_read" => new { status = OwnerAcknowledges && !(pendingRead && ReadCount == 1) ? "ok" : "blocked", chart = new { chart_index = args.GetProperty("chartIndex").GetInt32(),
                        symbol = OwnerMarketMismatch || pendingRead ? "WRONGUSDT" : _applied.GetValueOrDefault(args.GetProperty("chartIndex").GetInt32()).Symbol,
                        exchange = _applied.GetValueOrDefault(args.GetProperty("chartIndex").GetInt32()).Exchange, interval = "4h" } },
                    "chart_refresh" => new { workspaceId = "layout123", workspace = new { multiCharts = new {
                        mode = "4", layout = new { cols = 3, rows = 2, cells = new[] {
                            new { col = 0, row = 0, colSpan = ValidLayout ? 3 : 1, rowSpan = 1 },
                            new { col = 0, row = 1, colSpan = 1, rowSpan = 1 },
                            new { col = 1, row = 1, colSpan = 1, rowSpan = 1 },
                            new { col = 2, row = 1, colSpan = 1, rowSpan = 1 } } }, syncStatus = new { symbol = Sync, interval = false } } },
                        panes = _applied.Select(p => new { chartIndex = p.Key, symbol = p.Value.Symbol, exchange = p.Value.Exchange, interval = "4h" }).ToArray() },
                    _ => new { ok = true, load = new { state = Loaded ? "loaded" : "unknown" } }
                };
                if (name is "chart_symbol" or "chart_interval")
                {
                    if (!_lastStatusReady) WroteWhileStale = true;
                    Writes.Add(args.Clone());
                    if (name == "chart_symbol")
                    {
                        Symbols.Add(args.Clone());
                        if (!FailSymbol) _applied[args.GetProperty("chartIndex").GetInt32()] = (args.GetProperty("symbol").GetString()!, args.GetProperty("exchange").GetString()!);
                        if (DropSymbolReply) throw new HttpRequestException("The reply connection was lost.");
                    }
                    else Intervals.Add(args.GetProperty("interval").GetString());
                }
                if (name == "chart_symbol" && FailSymbol && SymbolErrorCode is not null)
                    data = new { error = new { code = SymbolErrorCode, message = SymbolErrorMessage } };
                response = new { jsonrpc = "2.0", id = root.GetProperty("id").GetInt32(), result = new {
                    isError = name == "chart_symbol" && FailSymbol, structuredContent = new { data } } };
            }
            // The real service may return JSON or SSE; exercise both in the same interaction.
            string json = JsonSerializer.Serialize(response);
            if (PersistentStream)
            {
                var streamContent = new StreamContent(new ReplyStream(Encoding.UTF8.GetBytes(": keep-alive\n\nevent: message\ndata: " + json + "\n\n")));
                streamContent.Headers.ContentType = new("text/event-stream");
                return new(HttpStatusCode.OK) { Content = streamContent };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(Writes.Count % 2 == 0 ? json : "event: message\ndata: " + json + "\n\n", Encoding.UTF8, Writes.Count % 2 == 0 ? "application/json" : "text/event-stream") };
        }
    }
    private sealed class ReplyStream(byte[] reply) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position == reply.Length) { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
            int count = Math.Min(buffer.Length, reply.Length - _position); reply.AsMemory(_position, count).CopyTo(buffer); _position += count; return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
