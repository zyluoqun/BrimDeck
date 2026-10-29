using System.IO;
using System.Net.Http;
using BrimDeck.Core;

namespace BrimDeck;

public partial class App
{
    private async Task SwitchCandleChartAsync(CandlePeriods period)
    {
        bool acquired = false;
        string step = "queue";
        LogChartRequest($"Countdown chart begin period={CandleCountdown.Label(period)}");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            await _chartOperationGate.WaitAsync(deadline.Token); acquired = true;
            if (!Settings.CandleCountdownChart || !CandleClock.IsSynchronized) return;
            var connection = ChartConnection;
            if (connection.Token.Length == 0) throw new InvalidOperationException(Loc.T("请先在设置 → 市场行情中配置图表联动。", "Configure the chart connection under Settings → Market first."));
            step = "resolve workspace";
            var catalog = new OpenMarketChartClient(_chartHttp, connection.Token, connection.WorkspaceId, LogChartRequest);
            string workspace = await catalog.ResolveWorkspaceAsync("BTC&ETH", deadline.Token);
            MarketChartStatus = Loc.T($"正在打开 BTC&ETH · {CandleCountdown.Label(period)}…", $"Opening BTC&ETH · {CandleCountdown.Label(period)}…"); MarketUpdated?.Invoke();
            step = "prepare Chrome window";
            _chromeCharts ??= new Native.ChromeChartBridge(diagnostic: LogChartRequest);
            await _chromeCharts.EnsureAsync(workspace, deadline.Token);
            step = "switch and confirm both panes";
            var pair = new OpenMarketChartClient(_chartHttp, connection.Token, workspace, LogChartRequest);
            await pair.SwitchPairAsync(period, deadline.Token);
            MarketChartStatus = Loc.T($"BTC&ETH · {CandleCountdown.Label(period)} · 网页已确认", $"BTC&ETH · {CandleCountdown.Label(period)} · confirmed by the page");
            LogChartRequest($"Countdown chart completed period={CandleCountdown.Label(period)}");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or HttpRequestException or System.Text.Json.JsonException
            or System.Security.Cryptography.CryptographicException or OperationCanceledException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or System.Security.SecurityException)
        {
            if (Exiting) return;
            MarketChartStatus = ex is OperationCanceledException or HttpRequestException
                ? Loc.T("BTC&ETH 自动打开超时或连接暂不可用，请检查网页和网络。", "Opening BTC&ETH timed out or the connection is unavailable; check the page and network.") : ex.Message;
            LogChartRequest($"Countdown chart failed period={CandleCountdown.Label(period)} step={step}: {MarketChartStatus}");
            _tray?.ShowNotification(Loc.T("收盘图表切换未完成", "Candle chart switch incomplete"), MarketChartStatus);
        }
        finally
        {
            if (acquired) _chartOperationGate.Release();
            if (!Exiting) MarketUpdated?.Invoke();
        }
    }
}
