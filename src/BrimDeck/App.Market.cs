using System.IO;
using System.Net.Http;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck;

public partial class App
{
    private readonly DispatcherTimer _marketTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly HttpClient _marketHttp = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(25) };
    private bool _marketBusy;
    internal MarketFeedSnapshot Market { get; private set; } = MarketFeedSnapshot.Empty;
    internal string? MarketError { get; private set; }
    internal event Action? MarketUpdated;
    private readonly HttpClient _chartHttp = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    private readonly object _chartLogGate = new();
    private void LogChartRequest(string message)
    {
        try
        {
            lock (_chartLogGate)
            {
                string path = Path.Combine(Store.DirectoryPath, "chart-link.log");
                if (File.Exists(path) && new FileInfo(path).Length > 131072) File.Move(path, path + ".previous", true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}\n");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private OpenMarketChartClient? _chartClient;
    private Native.ChromeChartBridge? _chromeCharts;
    private readonly SemaphoreSlim _chartOperationGate = new(1, 1);
    private readonly List<MarketCoin> _pendingChartCoins = [];
    internal IReadOnlyList<MarketCoin> PendingChartCoins => _pendingChartCoins;
    internal event Action? MarketChartActivityChanged;
    internal string? MarketChartStatus { get; private set; }
    internal Native.OpenMarketChartConnection ChartConnection => Native.OpenMarketChartConnection.Load(Store.DirectoryPath);
    internal void ConfigureMarketChart(string workspace, string token)
    {
        Native.OpenMarketChartConnection.Save(Store.DirectoryPath, workspace, token); _chartClient = null;
        MarketChartStatus = Loc.T("图表联动已配置", "Chart connection configured"); MarketUpdated?.Invoke();
    }
    internal Task SwitchMarketChartAsync(MarketCoin coin) => SwitchMarketChartAsync(coin, notice =>
        System.Windows.MessageBox.Show(Deck, notice, Loc.T("图表切换未完成", "Chart switch incomplete"), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information));
    internal async Task SwitchMarketChartAsync(MarketCoin coin, Action<string> showNotice)
    {
        _pendingChartCoins.Add(coin); MarketChartActivityChanged?.Invoke();
        bool acquired = false;
        string step = "queue";
        string? notice = null;
        try
        {
            await _chartOperationGate.WaitAsync(Lifetime.Token); acquired = true;
            var connection = ChartConnection;
            if (!Settings.MarketChartWorkspaces.TryGetValue(connection.WorkspaceId, out var savedRotation) || savedRotation.LayoutSignature.Length == 0)
                throw new InvalidOperationException(Loc.T("请先在行情设置中选择图表方案，检测布局并确认固定图表。", "Select a workspace, detect its layout and confirm its pinned charts in market settings first."));
            var rotation = savedRotation.Copy();
            if (_chartClient is null)
            {
                if (connection.Token.Length == 0) throw new InvalidOperationException(Loc.T("请先在设置 → 市场行情中配置图表联动", "Configure the chart connection under Settings → Market first"));
                _chartClient = new OpenMarketChartClient(_chartHttp, connection.Token, connection.WorkspaceId, LogChartRequest);
            }
            MarketChartStatus = Loc.T($"正在打开 {rotation.Name} 并切换 {coin.Asset}…", $"Opening {rotation.Name} and switching to {coin.Asset}…"); MarketUpdated?.Invoke();
            _chromeCharts ??= new Native.ChromeChartBridge(diagnostic: LogChartRequest);
            var chartClient = _chartClient;
            step = "verify workspace exists";
            var targetWorkspace = (await chartClient.ListWorkspacesAsync(Lifetime.Token)).FirstOrDefault(workspace => workspace.Id == connection.WorkspaceId);
            if (targetWorkspace is null)
                throw new InvalidOperationException(Loc.T("所选图表方案已不存在，请在行情设置中重新选择。", "The selected workspace no longer exists; select another in market settings."));
            rotation.Name = targetWorkspace.Name;
            step = "prepare Chrome window";
            LogChartRequest($"Market chart begin workspace={connection.WorkspaceId} symbol={coin.Symbol}");
            await _chromeCharts.EnsureAsync(connection.WorkspaceId, Lifetime.Token);
            // A queued click has not contacted the service yet. Only an active request has a timeout.
            step = "switch and confirm pane";
            var result = await chartClient.SwitchAsync(coin, Lifetime.Token, rotation);
            LogChartRequest($"Market chart completed workspace={connection.WorkspaceId} pane={result.ChartIndex} symbol={coin.Symbol}");
            MarketChartStatus = Loc.T($"{rotation.Name} · 图 {result.ChartIndex + 1} · {coin.Asset} · {rotation.Interval} · 网页已确认", $"{rotation.Name} · Chart {result.ChartIndex + 1} · {coin.Asset} · {rotation.Interval} · confirmed by the page");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or HttpRequestException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException or OperationCanceledException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or System.Security.SecurityException)
        {
            if (Exiting) return;
            MarketChartStatus = ex is OperationCanceledException or HttpRequestException
                ? Loc.T("图表联动连接超时或暂不可用，请检查网络和网页后重试", "Chart connection timed out or is unavailable; check the network and page, then retry") : ex.Message;
            LogChartRequest($"Market chart failed step={step}: {MarketChartStatus}");
            notice = MarketChartStatus;
        }
        finally
        {
            if (acquired) _chartOperationGate.Release();
            _pendingChartCoins.Remove(coin);
            if (!Exiting) { MarketChartActivityChanged?.Invoke(); MarketUpdated?.Invoke(); }
        }
        if (notice is not null && !Exiting)
            showNotice(notice);
    }
    private void StartMarketFeed()
    {
        _chromeCharts = new Native.ChromeChartBridge(diagnostic: LogChartRequest);
        try { Native.ChromeChartBridge.RegisterHost(Store.DirectoryPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { LogChartRequest("Chrome host registration failed: " + ex.GetType().Name); }
        _marketTimer.Tick += async (_, _) => await RefreshMarketAsync();
        _marketTimer.Start();
        _ = RefreshMarketAsync();
    }
    internal async Task RefreshMarketAsync()
    {
        if (_marketBusy || Exiting || !(Settings.MarketPage || Settings.CapsuleMarket || Settings.NotchMarket || Settings.LineMarket || Settings.CandleCountdownWanted)) return;
        _marketTimer.Interval = TimeSpan.FromSeconds(Decks.Any(deck => deck.MarketDemand) ? 15 : 60);
        _marketBusy = true;
        _candleRefreshPending = false;
        long clockStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var configuration = MarketFeedConfiguration.Load(AppContext.BaseDirectory);
            Market = await new MarketFeedClient(_marketHttp, configuration).ReadAsync(Lifetime.Token);
            if (Market.ServerTime is not { } serverTime || !CandleClock.Synchronize(serverTime, System.Diagnostics.Stopwatch.GetElapsedTime(clockStarted))) CandleClock.MarkUnavailable();
            MarketError = null;
        }
        catch (OperationCanceledException) { CandleClock.MarkUnavailable(); if (!Exiting) { Market = MarketFeedSnapshot.Empty; MarketError = Loc.T("行情连接超时", "Market connection timed out"); } }
        catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
        { CandleClock.MarkUnavailable(); Market = MarketFeedSnapshot.Empty; MarketError = ex is HttpRequestException ? Loc.T("行情服务暂不可用，请检查网络或只读授权", "Market service unavailable; check the network or read-only credential") : ex.Message; }
        finally
        {
            _marketBusy = false;
            if (!Exiting) { MarketUpdated?.Invoke(); CandleCountdownUpdated?.Invoke(); if (_candleRefreshPending) _ = RefreshMarketAsync(); }
        }
    }
}
