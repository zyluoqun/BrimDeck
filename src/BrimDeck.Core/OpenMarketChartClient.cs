using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Text;

namespace BrimDeck.Core;

public sealed record ChartSwitchResult(int ChartIndex, bool Loaded);
public sealed class ChartConnectionException(string operation, int? statusCode, bool timedOut, Exception cause)
    : InvalidOperationException(MessageFor(operation, statusCode, timedOut), cause)
{
    private static string MessageFor(string operation, int? code, bool timedOut)
    {
        string step = operation switch {
            "chart_symbol" => Loc.T("切换币种", "switching the market"), "chart_interval" => Loc.T("设置图表周期", "setting the chart interval"),
            "chart_refresh" => Loc.T("读取布局", "reading the layout"), _ => Loc.T("连接图表", "connecting the chart") };
        return code is { } status ? Loc.T($"OpenMarket 图表服务暂不可用（HTTP {status}，{step}），请稍后重试", $"OpenMarket chart service unavailable (HTTP {status}, {step}); try again shortly")
            : timedOut ? Loc.T($"图表联动请求超时（{step}），请稍后重试", $"Chart request timed out ({step}); try again shortly")
            : Loc.T($"图表联动网络连接失败（{step}），请检查网络后重试", $"Chart network connection failed ({step}); check the network and retry");
    }
}

// One connection and rotation are shared by every deck and by both market controls.
// Every mutation names the configured workspace and a lower pane; no browser navigation is involved.
public sealed partial class OpenMarketChartClient(HttpClient http, string token, string workspaceId, Action<string>? diagnostic = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _workspace = ParseWorkspace(workspaceId);
    private int _nextPane = 1, _request;
    private bool _initialized;
    private string? _session;
    public static string ParseWorkspace(string value)
    {
        value = value.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var url))
        {
            if (url.Scheme != "https" || url.Host != "openmarket.xyz" || !url.AbsolutePath.StartsWith("/chart/", StringComparison.Ordinal))
                throw new ArgumentException(Loc.T("请填写 OpenMarket 图表地址或布局 ID", "Enter an OpenMarket chart URL or workspace ID"));
            value = url.AbsolutePath[7..].TrimEnd('/');
        }
        if (!Regex.IsMatch(value, "^[A-Za-z0-9_-]{1,200}$"))
            throw new ArgumentException(Loc.T("OpenMarket 布局 ID 无效", "Invalid OpenMarket workspace ID"));
        return value;
    }
    public async Task<ChartSwitchResult> SwitchAsync(MarketCoin coin, CancellationToken cancellation, IReadOnlyCollection<int>? pinnedPanes = null)
    {
        _ = MarketCoin.ChartUrl(coin.Symbol, coin.Perpetual);
        var pinned = (pinnedPanes ?? []).Distinct().ToArray();
        if (pinned.Length > 2 || pinned.Any(pane => pane is < 1 or > 3))
            throw new ArgumentException(Loc.T("只能固定下方窗口 1、2、3，且最多固定两个", "Pin at most two of the lower panes 1, 2 and 3"), nameof(pinnedPanes));
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            await InitializeWorkspaceAsync(cancellation).ConfigureAwait(false);
            await WaitForPageAsync(cancellation).ConfigureAwait(false);
            var state = await CallAsync("chart_refresh", new { workspaceId = _workspace, full = true }, cancellation).ConfigureAwait(false);
            ValidateLayout(state);
            int pane = _nextPane;
            while (pinned.Contains(pane)) pane = pane == 3 ? 1 : pane + 1;
            diagnostic?.Invoke($"switch begin pane={pane} market={(coin.Perpetual ? "BINANCE_FUTURES" : "BINANCE")}:{coin.Symbol} interval=4h");
            await CallAsync("chart_interval", new { workspaceId = _workspace, chartIndex = pane, interval = "4h" }, cancellation).ConfigureAwait(false);
            JsonElement changed;
            try
            {
                changed = await CallAsync("chart_symbol", new { workspaceId = _workspace, chartIndex = pane,
                    symbol = coin.Symbol, exchange = coin.Perpetual ? "BINANCE_FUTURES" : "BINANCE" }, cancellation).ConfigureAwait(false);
            }
            catch (ChartConnectionException)
            {
                // A lost acknowledgement does not prove a failed mutation. Read the same pane back before advancing.
                if (!await ConfirmSwitchAsync(pane, coin, cancellation).ConfigureAwait(false)) throw;
                diagnostic?.Invoke($"chart_symbol acknowledgement recovered pane={pane}");
                _nextPane = pane == 3 ? 1 : pane + 1;
                return new(pane, true);
            }
            bool loaded = false;
            if (changed.TryGetProperty("load", out var load))
            {
                var loadState = Text(load, "state"); loaded = loadState == "loaded";
                diagnostic?.Invoke($"chart_symbol pane={pane} load={loadState} reason={Text(load, "reason")}");
                if (loadState is "empty" or "error") throw new InvalidOperationException(Loc.T("目标图表未能加载行情，请检查网页后重试", "The pane could not load this market; check the page and retry"));
            }
            // A REST save is not an execution acknowledgement from the user's open browser tab.
            if (!loaded && !await ConfirmSwitchAsync(pane, coin, cancellation).ConfigureAwait(false))
            {
                diagnostic?.Invoke($"switch unconfirmed pane={pane}; rotation retained");
                throw PageNotReady();
            }
            _nextPane = pane == 3 ? 1 : pane + 1;
            return new(pane, true);
        }
        finally { _gate.Release(); }
    }
    private async Task WaitForPageAsync(CancellationToken cancellation)
    {
        using var recovery = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        recovery.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                await RecoveryDelayAsync(attempt, recovery.Token).ConfigureAwait(false);
                var status = await CallAsync("chart_status", new { }, recovery.Token).ConfigureAwait(false);
                if (TryOwnerWorkspace(status, out _)) return;
                diagnostic?.Invoke($"page recovery attempt={attempt + 1}; waiting for live state sync");
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { diagnostic?.Invoke("page recovery deadline reached; rotation retained"); }
        throw PageNotReady();
    }
    private async Task<bool> ConfirmSwitchAsync(int pane, MarketCoin coin, CancellationToken cancellation, string interval = "4h")
    {
        using var recovery = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        recovery.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                await RecoveryDelayAsync(attempt, recovery.Token).ConfigureAwait(false);
                if (await TryConfirmSwitchAsync(pane, coin, recovery.Token, interval).ConfigureAwait(false)) return true;
                diagnostic?.Invoke($"browser confirmation pending pane={pane} attempt={attempt + 1}; rotation retained");
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { diagnostic?.Invoke($"browser confirmation deadline reached pane={pane}; rotation retained"); }
        return false;
    }
    private static Task RecoveryDelayAsync(int attempt, CancellationToken cancellation) => attempt == 0
        ? Task.CompletedTask : Task.Delay(TimeSpan.FromMilliseconds(500 * (1 << (attempt - 1))), cancellation);
    private async Task<bool> TryConfirmSwitchAsync(int pane, MarketCoin coin, CancellationToken cancellation, string interval)
    {
        try
        {
            var current = await CallAsync("chart_status", new { }, cancellation).ConfigureAwait(false);
            if (!TryOwnerWorkspace(current, out _)) return false;
            // chart_read asks the browser tab itself. A stored workspace or an old observed pane is not sufficient.
            var live = await CallAsync("chart_read", new { chartIndex = pane }, cancellation).ConfigureAwait(false);
            if (Text(live, "status") != "ok" || !live.TryGetProperty("chart", out var chart))
            {
                diagnostic?.Invoke($"browser confirmation blocked pane={pane} status={Text(live, "status")} reason={Text(live, "reason")}");
                return false;
            }
            diagnostic?.Invoke($"browser confirmation pane={pane} market={Text(chart, "exchange")}:{Text(chart, "symbol")} interval={Text(chart, "interval")}");
            return Number(chart, "chart_index") == pane && Text(chart, "symbol") == coin.Symbol
                && Text(chart, "exchange") == (coin.Perpetual ? "BINANCE_FUTURES" : "BINANCE") && Text(chart, "interval") == interval;
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or JsonException) { return false; }
    }
    private bool TryOwnerWorkspace(JsonElement status, out JsonElement workspace)
    {
        workspace = default;
        if (!status.TryGetProperty("workspaces", out var spaces) || spaces.ValueKind != JsonValueKind.Array) return false;
        foreach (var item in spaces.EnumerateArray())
            if (Text(item, "workspaceId") == _workspace && Text(item, "state") == "open" && Flag(item, "humanPresent") && Flag(item, "hasStateSync") && !Flag(item, "stateStale"))
            { workspace = item; return true; }
        return false;
    }
    private static InvalidOperationException PageNotReady() => new(Loc.T("已等待网页连接恢复，但实时连接仍未就绪或尚未确认执行。请切回已打开的四屏网页，必要时刷新后重试；轮换位置已保留。", "Waited for the chart page to recover, but its live connection is still unavailable or execution is unconfirmed. Return to the existing four-pane page; refresh it if needed, then retry. The rotation position is retained."));
    private void ValidateLayout(JsonElement state)
    {
        bool valid = Text(state, "workspaceId") == _workspace && state.TryGetProperty("workspace", out var workspace)
            && workspace.TryGetProperty("multiCharts", out var charts) && Text(charts, "mode") == "4";
        if (!valid) throw BadLayout();
        var multi = state.GetProperty("workspace").GetProperty("multiCharts");
        if (!multi.TryGetProperty("layout", out var layout) || !layout.TryGetProperty("cells", out var cells) || cells.ValueKind != JsonValueKind.Array
            || cells.GetArrayLength() != 4 || Number(layout, "cols") != 3 || Number(layout, "rows") != 2)
            throw BadLayout();
        for (int i = 0; i < 4; i++)
        {
            var cell = cells[i];
            if (Number(cell, "col") != (i == 0 ? 0 : i - 1) || Number(cell, "row") != (i == 0 ? 0 : 1)
                || Number(cell, "colSpan") != (i == 0 ? 3 : 1) || Number(cell, "rowSpan") != 1)
                throw BadLayout();
        }
        if (!multi.TryGetProperty("syncStatus", out var sync) || !sync.TryGetProperty("symbol", out var symbolSync) || symbolSync.ValueKind != JsonValueKind.False
            || !sync.TryGetProperty("interval", out var intervalSync) || intervalSync.ValueKind != JsonValueKind.False)
            throw new InvalidOperationException(Loc.T("请先关闭四屏布局的币种同步和周期同步，以保持上方图表不变", "Turn off symbol and interval sync in this workspace to keep the top chart unchanged"));
    }
    private static InvalidOperationException BadLayout() => new(Loc.T("目标布局需为上方一屏、下方三屏；请检查网页布局", "The target layout must have one chart above three charts; check the page layout"));
    private async Task<JsonElement> CallAsync(string name, object arguments, CancellationToken cancellation)
    {
        var result = await RpcAsync("tools/call", new { name, arguments }, cancellation).ConfigureAwait(false);
        JsonElement data;
        if (result.TryGetProperty("structuredContent", out var structured) && structured.TryGetProperty("data", out data)) { }
        else if (result.TryGetProperty("content", out var content) && content.EnumerateArray().FirstOrDefault(c => Text(c, "type") == "text") is var text && text.ValueKind == JsonValueKind.Object)
        { using var document = JsonDocument.Parse(Text(text, "text")); data = document.RootElement.Clone(); }
        else throw new InvalidOperationException(Loc.T("OpenMarket 返回了无法识别的响应", "Unrecognized OpenMarket response"));
        if (Flag(result, "isError") || data.ValueKind == JsonValueKind.Object && (data.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False || data.TryGetProperty("error", out _)))
        {
            var error = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("error", out var problem) ? problem : default;
            string code = Text(error, "code");
            if (code.Length == 0) code = Text(data, "code");
            if (code.Length == 0) code = Text(data, "reason");
            if (code.Length == 0 && error.ValueKind == JsonValueKind.String) code = error.GetString() ?? "";
            string detail = Text(error, "message");
            if (detail.Length == 0) detail = Text(data, "message");
            string Safe(string value) => (token.Length > 0 ? value.Replace(token, "[redacted]", StringComparison.Ordinal) : value).Replace('\r', ' ').Replace('\n', ' ');
            code = Safe(code); detail = Safe(detail);
            if (code.Length > 120) code = code[..120];
            if (detail.Length > 400) detail = detail[..400];
            diagnostic?.Invoke($"{name} refused code={code} message={detail}");
            if (name == "chart_symbol" && code == "VALIDATION" && detail.Contains("symbol must be alphanumeric", StringComparison.OrdinalIgnoreCase)
                && Text(JsonSerializer.SerializeToElement(arguments), "symbol").Any(c => c > 127))
                throw new InvalidOperationException(Loc.T("OpenMarket 的切币接口暂不接受中文交易对，请在四屏网页中手动选择该币种；轮换位置已保留。", "OpenMarket's chart-switch API currently rejects non-English symbols. Select this market in the chart page; the rotation position is retained."));
            throw new InvalidOperationException(code.Length + detail.Length == 0
                ? Loc.T("OpenMarket 拒绝了图表操作，请检查授权和网页状态", "OpenMarket refused the chart operation; check authorization and the page")
                : Loc.T($"OpenMarket 拒绝图表操作（{code}）：{detail}", $"OpenMarket refused the chart operation ({code}): {detail}"));
        }
        return data;
    }
    private async Task<JsonElement> RpcAsync(string method, object parameters, CancellationToken cancellation)
    {
        string operation = method == "tools/call" ? JsonSerializer.SerializeToElement(parameters).GetProperty("name").GetString()! : method;
        bool read = operation is "initialize" or "chart_status" or "chart_refresh" or "chart_read" or "chart_list";
        for (int attempt = 0; ; attempt++)
        {
            var timer = Stopwatch.StartNew();
            try
            {
                var result = await RpcOnceAsync(method, parameters, cancellation).ConfigureAwait(false);
                diagnostic?.Invoke($"{operation} attempt={attempt + 1} ok elapsedMs={timer.ElapsedMilliseconds}");
                return result;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException && !cancellation.IsCancellationRequested)
            {
                int? status = ex is HttpRequestException requestError && requestError.StatusCode is { } code ? (int)code : null;
                diagnostic?.Invoke($"{operation} attempt={attempt + 1} http={status?.ToString() ?? "none"} error={ex.GetType().Name} elapsedMs={timer.ElapsedMilliseconds}");
                if (!read || attempt >= 2 || status is not (null or 408 or 429 or 502 or 503 or 504))
                    throw new ChartConnectionException(operation, status, ex is OperationCanceledException, ex);
                await Task.Delay(TimeSpan.FromMilliseconds(attempt == 0 ? 200 : 600), cancellation).ConfigureAwait(false);
            }
        }
    }
    private async Task<JsonElement> RpcOnceAsync(string method, object parameters, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        cancellation = deadline.Token;
        int requestId = ++_request;
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://mcp.openmarket.xyz/")
        { Content = JsonContent.Create(new { jsonrpc = "2.0", id = requestId, method, @params = parameters }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("User-Agent", "BrimDeck/1.0 Mozilla/5.0");
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        if (_session is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _session);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException(Loc.T("图表授权已失效或权限不足，请在行情设置中更新令牌", "Chart authorization expired or is insufficient; update the token in market settings"));
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException(Loc.T("图表操作过于频繁，请稍后再试", "Too many chart operations; try again shortly"));
        response.EnsureSuccessStatusCode();
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var sessions)) _session = sessions.FirstOrDefault();
        string text;
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false));
            var data = new StringBuilder(); text = "";
            while (await reader.ReadLineAsync(cancellation).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0 && data.Length > 0)
                {
                    using var message = JsonDocument.Parse(data.ToString());
                    if (Number(message.RootElement, "id") == requestId) { text = data.ToString(); break; }
                    data.Clear();
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal)) data.AppendLine(line[5..].TrimStart());
                if (data.Length > 4_000_000) throw new InvalidOperationException("OpenMarket response exceeds the chart message limit.");
            }
            if (text.Length == 0 && data.Length > 0) text = data.ToString();
        }
        else text = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
        using var document = JsonDocument.Parse(text);
        if (!document.RootElement.TryGetProperty("result", out var result))
            throw new InvalidOperationException(Loc.T("OpenMarket 图表接口返回错误", "OpenMarket chart API returned an error"));
        return result.Clone();
    }
    private static string Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
    private static bool Flag(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;
    private static int Number(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out int number) ? number : -1;
}
