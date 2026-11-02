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
    public bool IsTransient => statusCode is null or 408 or 429 or 502 or 503 or 504;
    private static string MessageFor(string operation, int? code, bool timedOut)
    {
        string step = operation switch {
            "initialize" => Loc.T("建立图表连接", "establishing the chart connection"), "chart_workspace_select" => Loc.T("选择图表布局", "selecting the chart workspace"),
            "chart_symbol" => Loc.T("切换币种", "switching the market"), "chart_interval" => Loc.T("设置图表周期", "setting the chart interval"),
            "chart_refresh" => Loc.T("读取布局", "reading the layout"), _ => Loc.T("连接图表", "connecting the chart") };
        return code is { } status ? Loc.T($"OpenMarket 图表服务暂不可用（HTTP {status}，{step}），请稍后重试", $"OpenMarket chart service unavailable (HTTP {status}, {step}); try again shortly")
            : timedOut ? Loc.T($"图表联动请求超时（{step}），请稍后重试", $"Chart request timed out ({step}); try again shortly")
            : Loc.T($"图表联动网络连接失败（{step}），请检查网络后重试", $"Chart network connection failed ({step}); check the network and retry");
    }
}

// One connection and rotation are shared by every deck and by both market controls.
// Every mutation names the configured workspace and a confirmed, unpinned pane.
public sealed partial class OpenMarketChartClient(HttpClient http, string token, string workspaceId, Action<string>? diagnostic = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _workspace = ParseWorkspace(workspaceId);
    private int _nextPane, _request;
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
    public async Task<ChartSwitchResult> SwitchAsync(MarketCoin coin, CancellationToken cancellation, ChartRotationSettings rotation)
    {
        _ = MarketCoin.ChartUrl(coin.Symbol, coin.Perpetual);
        var pinned = (rotation.PinnedPanes ?? []).Distinct().ToArray();
        string interval = ChartIntervals.Normalize(rotation.Interval);
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            await SelectWorkspaceAsync(cancellation).ConfigureAwait(false);
            await WaitForPageAsync(cancellation).ConfigureAwait(false);
            var state = await CallAsync("chart_refresh", new { workspaceId = _workspace, full = true }, cancellation).ConfigureAwait(false);
            var layout = ChartLayout.Parse(state, _workspace);
            // With no pinned charts there are no saved pane positions to protect. Use the live layout.
            if (!rotation.AcceptsLayout(layout))
                throw new InvalidOperationException(Loc.T("方案布局尚未确认或已发生变化。请在行情设置中重新检测布局、确认固定图表后再切换。", "The workspace layout is unconfirmed or has changed. Detect the layout and confirm its pinned charts in market settings before switching."));
            if (pinned.Any(pane => pane < 0 || pane >= layout.Panes.Count))
                throw new ArgumentException(Loc.T("固定图表不在当前布局中，请重新确认。", "A pinned chart is outside this layout; confirm the layout again."), nameof(rotation));
            var available = layout.Panes.Select(p => p.Index).Except(pinned).ToArray();
            if (available.Length == 0) throw new InvalidOperationException(Loc.T("此方案的图表已全部固定，切换已暂停。请先取消固定至少一张图表。", "All charts in this workspace are pinned. Unpin at least one to resume switching."));
            if (layout.Panes.Count > 1 && (layout.SymbolSync || layout.IntervalSync))
                throw new InvalidOperationException(Loc.T("请先在网页关闭此方案的币种同步和周期同步，避免联动修改其他图表。", "Turn off symbol and interval sync in this workspace before switching to avoid changing other charts."));
            int pane = available.FirstOrDefault(p => p >= _nextPane, available[0]);
            diagnostic?.Invoke($"switch begin pane={pane} market={(coin.Perpetual ? "BINANCE_FUTURES" : "BINANCE")}:{coin.Symbol} interval={interval}");
            await CallAsync("chart_interval", new { workspaceId = _workspace, chartIndex = pane, interval }, cancellation).ConfigureAwait(false);
            JsonElement changed;
            try
            {
                changed = await CallAsync("chart_symbol", new { workspaceId = _workspace, chartIndex = pane,
                    symbol = coin.Symbol, exchange = coin.Perpetual ? "BINANCE_FUTURES" : "BINANCE" }, cancellation).ConfigureAwait(false);
            }
            catch (ChartConnectionException)
            {
                // A lost acknowledgement does not prove a failed mutation. Read the same pane back before advancing.
                if (!await ConfirmSwitchAsync(pane, coin, cancellation, interval).ConfigureAwait(false)) throw;
                diagnostic?.Invoke($"chart_symbol acknowledgement recovered pane={pane}");
                _nextPane = (pane + 1) % layout.Panes.Count;
                return new(pane, true);
            }
            if (changed.TryGetProperty("load", out var load))
            {
                var loadState = Text(load, "state");
                diagnostic?.Invoke($"chart_symbol pane={pane} load={loadState} reason={Text(load, "reason")}");
                if (loadState is "empty" or "error") throw new InvalidOperationException(Loc.T("目标图表未能加载行情，请检查网页后重试", "The pane could not load this market; check the page and retry"));
            }
            // A loaded report alone does not verify both the market and interval in the live browser tab.
            if (!await ConfirmSwitchAsync(pane, coin, cancellation, interval).ConfigureAwait(false))
            {
                diagnostic?.Invoke($"switch unconfirmed pane={pane}; rotation retained");
                throw PageNotReady();
            }
            _nextPane = (pane + 1) % layout.Panes.Count;
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
    private static InvalidOperationException PageNotReady() => new(Loc.T("已等待网页连接恢复，但实时连接仍未就绪或尚未确认执行。请切回已打开的目标方案网页，必要时刷新后重试；轮换位置已保留。", "Waited for the chart page to recover, but its live connection is still unavailable or execution is unconfirmed. Return to the existing chart workspace; refresh it if needed, then retry. The rotation position is retained."));
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
                throw new InvalidOperationException(Loc.T("OpenMarket 的切币接口暂不接受中文交易对，请在目标方案网页中手动选择该币种；轮换位置已保留。", "OpenMarket's chart-switch API currently rejects non-English symbols. Select this market in the chart page; the rotation position is retained."));
            throw new InvalidOperationException(code.Length + detail.Length == 0
                ? Loc.T("OpenMarket 拒绝了图表操作，请检查授权和网页状态", "OpenMarket refused the chart operation; check authorization and the page")
                : Loc.T($"OpenMarket 拒绝图表操作（{code}）：{detail}", $"OpenMarket refused the chart operation ({code}): {detail}"));
        }
        return data;
    }
    private async Task<JsonElement> RpcAsync(string method, object parameters, CancellationToken cancellation)
    {
        string operation = method == "tools/call" ? JsonSerializer.SerializeToElement(parameters).GetProperty("name").GetString()! : method;
        // Selecting the same workspace is idempotent; market and interval mutations must never be resent automatically.
        bool retryable = operation is "initialize" or "chart_workspace_select" or "chart_status" or "chart_refresh" or "chart_read" or "chart_list";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        for (int attempt = 0; ; attempt++)
        {
            var timer = Stopwatch.StartNew();
            try
            {
                if (attempt > 0)
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * (1 << (attempt - 1))), deadline.Token).ConfigureAwait(false);
                timer.Restart();
                // A hung connection must leave time for a fresh attempt within the total budget.
                // Writes retain their full timeout and are never replayed automatically.
                using var attemptDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                if (retryable) attemptDeadline.CancelAfter(TimeSpan.FromSeconds(6));
                var result = await RpcOnceAsync(method, parameters, attemptDeadline.Token).ConfigureAwait(false);
                diagnostic?.Invoke($"{operation} attempt={attempt + 1} ok elapsedMs={timer.ElapsedMilliseconds}");
                return result;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException && !cancellation.IsCancellationRequested)
            {
                int? status = ex is HttpRequestException requestError && requestError.StatusCode is { } code ? (int)code : null;
                string kind = ex is HttpRequestException networkError ? networkError.HttpRequestError.ToString() : "Timeout";
                diagnostic?.Invoke($"{operation} attempt={attempt + 1} http={status?.ToString() ?? "none"} error={ex.GetType().Name} kind={kind} cause={ex.GetBaseException().GetType().Name} elapsedMs={timer.ElapsedMilliseconds}");
                if (!retryable || attempt >= 4 || deadline.IsCancellationRequested || status is not (null or 408 or 429 or 502 or 503 or 504))
                    throw new ChartConnectionException(operation, status, ex is OperationCanceledException, ex);
            }
        }
    }
    private async Task<JsonElement> RpcOnceAsync(string method, object parameters, CancellationToken cancellation)
    {
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
