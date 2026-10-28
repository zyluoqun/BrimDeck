using System.Text.Json;

namespace BrimDeck.Core;

public sealed partial class OpenMarketChartClient
{
    private bool _sessionInitialized;
    private async Task InitializeSessionAsync(CancellationToken cancellation)
    {
        if (_sessionInitialized) return;
        await RpcAsync("initialize", new { protocolVersion = "2025-03-26", capabilities = new { },
            clientInfo = new { name = "BrimDeck", version = "1.0" } }, cancellation).ConfigureAwait(false);
        _sessionInitialized = true;
    }
    private async Task InitializeWorkspaceAsync(CancellationToken cancellation)
    {
        if (_initialized) return;
        await InitializeSessionAsync(cancellation).ConfigureAwait(false);
        await CallAsync("chart_workspace_select", new { workspaceId = _workspace }, cancellation).ConfigureAwait(false);
        _initialized = true;
    }
    public async Task<string> ResolveWorkspaceAsync(string name, CancellationToken cancellation)
    {
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            await InitializeSessionAsync(cancellation).ConfigureAwait(false);
            var list = await CallAsync("chart_list", new { }, cancellation).ConfigureAwait(false);
            var matches = list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Where(item => Text(item, "name") == name).ToArray() : [];
            if (matches.Length != 1) throw new InvalidOperationException(matches.Length == 0
                ? Loc.T($"未找到名为「{name}」的布局，请先在 OpenMarket 中创建。", $"Create the '{name}' layout in OpenMarket first.")
                : Loc.T($"存在多个名为「{name}」的布局，请保留一个同名布局后重试。", $"Several layouts are named '{name}'; give them unique names and retry."));
            string id = Text(matches[0], "shortId");
            return ParseWorkspace(id.Length > 0 ? id : Text(matches[0], "id"));
        }
        finally { _gate.Release(); }
    }
    public async Task SwitchPairAsync(CandlePeriods period, CancellationToken cancellation)
    {
        string interval = period switch { CandlePeriods.OneHour => "1h", CandlePeriods.FourHours => "4h", _ => throw new ArgumentException("Only 1H and 4H can open the BTC&ETH chart.", nameof(period)) };
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            await InitializeWorkspaceAsync(cancellation).ConfigureAwait(false);
            await WaitForPageAsync(cancellation).ConfigureAwait(false);
            var state = await CallAsync("chart_refresh", new { workspaceId = _workspace, full = true }, cancellation).ConfigureAwait(false);
            if (Text(state, "workspaceId") != _workspace || !state.TryGetProperty("workspace", out var workspace)
                || !workspace.TryGetProperty("multiCharts", out var multi) || !multi.TryGetProperty("syncStatus", out var sync)
                || !sync.TryGetProperty("symbol", out var symbolSync) || symbolSync.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidOperationException(Loc.T("无法确认 BTC&ETH 布局状态，请检查网页后重试。", "Could not verify the BTC&ETH layout; check the page and retry."));
            if (symbolSync.ValueKind == JsonValueKind.True)
                await CallAsync("chart_sync", new { workspaceId = _workspace, key = "symbol", enabled = false }, cancellation).ConfigureAwait(false);
            if (!IsPairLayout(multi))
                await CallAsync("chart_layout", new { workspaceId = _workspace, mode = "2H" }, cancellation).ConfigureAwait(false);
            MarketCoin[] coins = [new("BTC", "BTCUSDT", true, null, null), new("ETH", "ETHUSDT", true, null, null)];
            for (int pane = 0; pane < coins.Length; pane++)
            {
                await CallAsync("chart_interval", new { workspaceId = _workspace, chartIndex = pane, interval }, cancellation).ConfigureAwait(false);
                try
                {
                    await CallAsync("chart_symbol", new { workspaceId = _workspace, chartIndex = pane,
                        symbol = coins[pane].Symbol, exchange = "BINANCE_FUTURES" }, cancellation).ConfigureAwait(false);
                }
                catch (ChartConnectionException)
                {
                    if (!await ConfirmSwitchAsync(pane, coins[pane], cancellation, interval).ConfigureAwait(false)) throw;
                }
            }
            // A save/load acknowledgement alone cannot confirm that both charts have the requested interval.
            for (int pane = 0; pane < coins.Length; pane++)
                if (!await ConfirmSwitchAsync(pane, coins[pane], cancellation, interval).ConfigureAwait(false))
                    throw new InvalidOperationException(Loc.T("BTC&ETH 网页尚未确认币种和周期，请检查图表连接。", "The BTC&ETH page has not confirmed its markets and intervals; check the chart connection."));
            diagnostic?.Invoke($"BTC&ETH confirmed workspace={_workspace} interval={interval}");
        }
        finally { _gate.Release(); }
    }
    private static bool IsPairLayout(JsonElement multi)
    {
        if (Text(multi, "mode") != "2H") return false;
        if (!multi.TryGetProperty("layout", out var layout)) return true; // Standard 2H also supports an implicit layout.
        if (Number(layout, "cols") != 2 || Number(layout, "rows") != 1 || !layout.TryGetProperty("cells", out var cells)
            || cells.ValueKind != JsonValueKind.Array || cells.GetArrayLength() != 2) return false;
        for (int pane = 0; pane < 2; pane++)
            if (Number(cells[pane], "col") != pane || Number(cells[pane], "row") != 0
                || Number(cells[pane], "colSpan") != 1 || Number(cells[pane], "rowSpan") != 1) return false;
        return true;
    }
}
