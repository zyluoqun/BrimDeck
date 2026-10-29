using System.Net;
using System.Text.Json;
using BrimDeck.Core;

internal static class OpenMarketPairTests
{
    internal static async Task Run(Action<string, bool> check)
    {
        using var handler = new PairHandler();
        using var http = new HttpClient(handler);
        var client = new OpenMarketChartClient(http, "test-token", "pair123");
        check("The countdown resolves the exact named workspace instead of the four-pane workspace",
            await client.ResolveWorkspaceAsync("BTC&ETH", default) == "pair123");
        handler.Names = ["BTC&ETH", "BTC&ETH"];
        bool rejected = false;
        try { await client.ResolveWorkspaceAsync("BTC&ETH", default); } catch (InvalidOperationException) { rejected = true; }
        check("Duplicate workspace names are refused rather than selecting an arbitrary layout", rejected && handler.Writes.Count == 0);
        handler.Names = ["Other"];
        rejected = false;
        try { await client.ResolveWorkspaceAsync("BTC&ETH", default); } catch (InvalidOperationException) { rejected = true; }
        check("An absent named workspace is reported without modifying another layout", rejected && handler.Writes.Count == 0);
        handler.Names = ["BTC&ETH"];
        await client.SwitchPairAsync(CandlePeriods.OneHour, default);
        check("The BTC and ETH countdown turns a single chart into two horizontal panes", handler.Mode == "2H");
        check("Symbol synchronization is disabled before either market changes", handler.Writes.IndexOf("chart_sync") < handler.Writes.IndexOf("chart_symbol") && !handler.SymbolSync);
        check("The left pane is Binance BTC perpetual and the right is Binance ETH perpetual", handler.Panes[0].Symbol == "BTCUSDT"
            && handler.Panes[1].Symbol == "ETHUSDT" && handler.Panes.Values.All(p => p.Exchange == "BINANCE_FUTURES" && p.Interval == "1h"));
        check("Pair switching verifies both panes in the live browser", handler.Reads.SequenceEqual([0, 1]));
        int layouts = handler.Writes.Count(w => w == "chart_layout");
        await client.SwitchPairAsync(CandlePeriods.FourHours, default);
        check("The four-hour close sets both charts to 4h without rebuilding the saved two-pane layout",
            handler.Panes.Values.All(p => p.Interval == "4h") && handler.Writes.Count(w => w == "chart_layout") == layouts);
        check("All pair mutations explicitly target the named workspace", handler.Targets.All(w => w == "pair123"));
        int writes = handler.Writes.Count;
        rejected = false;
        try { await client.SwitchPairAsync(CandlePeriods.FifteenMinutes, default); } catch (ArgumentException) { rejected = true; }
        check("A fifteen-minute event is refused before contacting the chart service", rejected && handler.Writes.Count == writes);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await client.SwitchPairAsync(CandlePeriods.OneHour, cancelled.Token); } catch (OperationCanceledException) { }
        check("Cancelled pair switching does not write to the layout", handler.Writes.Count == writes);
        handler.BlockReads = true;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        bool confirmed = false;
        try { await client.SwitchPairAsync(CandlePeriods.OneHour, deadline.Token); confirmed = true; }
        catch (OperationCanceledException) { }
        check("Stored state cannot claim success while live pane confirmation is blocked", !confirmed);
        handler.BlockReads = false;
        handler.SelectedWorkspace = "layout123";
        using var reselectionDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        bool reselected = false;
        try { await client.SwitchPairAsync(CandlePeriods.FourHours, reselectionDeadline.Token); reselected = true; }
        catch (OperationCanceledException) { }
        check("A reused countdown client reselects BTC&ETH after a market click moved the shared session",
            reselected && handler.SelectedWorkspace == "pair123" && handler.Panes.Values.All(p => p.Interval == "4h"));
    }

    private sealed class PairHandler : HttpMessageHandler
    {
        internal string[] Names = ["BTC&ETH"];
        internal string Mode = "1";
        internal string SelectedWorkspace = "";
        internal bool SymbolSync = true, BlockReads;
        internal List<string> Writes = [], Targets = [];
        internal List<int> Reads = [];
        internal Dictionary<int, (string Symbol, string Exchange, string Interval)> Panes = new() { [0] = ("BTC-USD", "COINBASE", "1h") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = body.RootElement;
            object result;
            if (root.GetProperty("method").GetString() == "initialize") result = new { protocolVersion = "2025-03-26" };
            else
            {
                var p = root.GetProperty("params"); var name = p.GetProperty("name").GetString()!; var args = p.GetProperty("arguments");
                if (args.TryGetProperty("workspaceId", out var id)) Targets.Add(id.GetString()!);
                object data;
                switch (name)
                {
                    case "chart_workspace_select": SelectedWorkspace = args.GetProperty("workspaceId").GetString()!;
                        data = new { workspaceId = SelectedWorkspace, switched = true }; break;
                    case "chart_list": data = Names.Select((name, index) => new { name, shortId = index == 0 ? "pair123" : "pair456" }).ToArray(); break;
                    case "chart_status": data = new { workspaces = new[] { new { workspaceId = SelectedWorkspace, state = "open", humanPresent = true, hasStateSync = true, stateStale = false } } }; break;
                    case "chart_refresh": data = new { workspaceId = "pair123", workspace = new { multiCharts = new { mode = Mode,
                        layout = new { cols = 2, rows = 1, cells = new[] { new { col = 0, row = 0, colSpan = 1, rowSpan = 1 }, new { col = 1, row = 0, colSpan = 1, rowSpan = 1 } } },
                        syncStatus = new { symbol = SymbolSync, interval = false } } } }; break;
                    case "chart_read":
                        int read = args.GetProperty("chartIndex").GetInt32(); Reads.Add(read); var pane = Panes[read];
                        data = new { status = BlockReads ? "blocked" : "ok", chart = new { chart_index = read, symbol = pane.Symbol, exchange = pane.Exchange, interval = pane.Interval } }; break;
                    default:
                        if (name is "chart_layout" or "chart_sync" or "chart_interval" or "chart_symbol") Writes.Add(name);
                        if (name == "chart_layout") { Mode = args.GetProperty("mode").GetString()!; Panes[1] = Panes[0]; }
                        if (name == "chart_sync") SymbolSync = args.GetProperty("enabled").GetBoolean();
                        if (name is "chart_interval" or "chart_symbol")
                        {
                            int index = args.GetProperty("chartIndex").GetInt32(); var prior = Panes[index];
                            Panes[index] = name == "chart_interval" ? (prior.Symbol, prior.Exchange, args.GetProperty("interval").GetString()!)
                                : (args.GetProperty("symbol").GetString()!, args.GetProperty("exchange").GetString()!, prior.Interval);
                        }
                        data = new { ok = true, load = new { state = "loaded" } }; break;
                }
                result = new { structuredContent = new { data } };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = root.GetProperty("id").GetInt32(), result }), System.Text.Encoding.UTF8, "application/json") };
        }
    }
}
