using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck;

public partial class App
{
    internal CandleClock CandleClock { get; } = new();
    private readonly CandleCloseGate _candleCloses = new();
    private readonly DispatcherTimer _candleTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    internal event Action? CandleCountdownUpdated;
    private bool _candleStarted, _candleWasSynchronized;
    private bool _candleRefreshPending;

    private void StartCandleCountdown()
    {
        _candleStarted = true;
        _candleTimer.Tick += (_, _) => TickCandleCountdown();
        Microsoft.Win32.SystemEvents.PowerModeChanged += CandlePowerChanged;
        UpdateCandleCountdownSettings();
    }
    private void UpdateCandleCountdownSettings()
    {
        if (!_candleStarted) return;
        _candleCloses.Reset(); _candleWasSynchronized = false;
        if (Settings.CandleCountdownWanted) _candleTimer.Start(); else _candleTimer.Stop();
        TickCandleCountdown();
    }
    private void TickCandleCountdown()
    {
        if (Exiting) return;
        var now = CandleClock.UtcNow;
        bool synchronized = CandleClock.IsSynchronized;
        if (!synchronized || !_candleWasSynchronized) _candleCloses.Reset();
        var closed = _candleCloses.Observe(now, Settings.CandleCountdownWanted ? Settings.CandleCountdownPeriods : CandlePeriods.None);
        _candleWasSynchronized = synchronized;
        CandleCountdownUpdated?.Invoke();
        if (closed.Count > 0 && synchronized)
        {
            string periods = string.Join(" / ", closed.Select(CandleCountdown.Label));
            if (Settings.CandleCountdownSound) System.Media.SystemSounds.Asterisk.Play();
            if (Settings.CandleCountdownNotification) _tray?.ShowNotification(Loc.T("K 线已收盘", "Candle closed"),
                Loc.T($"{periods} 已收盘，已开始下一根 K 线。", $"{periods} closed. The next candle has started."));
            var chartPeriod = CandleCountdown.ChartPeriod(closed);
            if (Settings.CandleCountdownChart && chartPeriod != CandlePeriods.None) _ = SwitchCandleChartAsync(chartPeriod);
        }
        _candleTimer.Interval = TimeSpan.FromMilliseconds(1000 - now.Millisecond + 25);
    }
    private void CandlePowerChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Resume || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            CandleClock.MarkUnavailable(); _candleCloses.Reset(); _candleWasSynchronized = false;
            _candleRefreshPending = true;
            TickCandleCountdown(); _ = RefreshMarketAsync();
        });
    }
    private void StopCandleCountdown()
    {
        _candleTimer.Stop();
        Microsoft.Win32.SystemEvents.PowerModeChanged -= CandlePowerChanged;
    }
}
