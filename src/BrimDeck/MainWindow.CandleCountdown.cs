using System.Windows;
using System.Windows.Controls;
using BrimDeck.Core;

namespace BrimDeck;

public partial class MainWindow
{
    private CandleCountdownStrip? _compactCountdown;
    private void InitializeCandleCountdown()
    {
        _app.CandleCountdownUpdated += UpdateCandleCountdown;
        Closed += (_, _) => _app.CandleCountdownUpdated -= UpdateCandleCountdown;
    }
    private void UpdateCandleCountdown()
    {
        if (!IsVisible || IsSuppressed) return;
        var now = _app.CandleClock.UtcNow;
        if (!_expanded && _alert is null) _compactCountdown?.Update(now, _app.CandleClock.IsSynchronized, Settings.CandleCountdownHighlight);
        if (_expanded && IsMarketPage) _marketPageView?.UpdateCountdown(Settings, now, _app.CandleClock.IsSynchronized);
    }
    private CandleCountdownStrip? CreateCandleCountdown(double scale)
    {
        if (!Settings.ShowsCandleCountdown(EffectiveStyle)) return null;
        var strip = new CandleCountdownStrip(Settings.CandleCountdownPeriods, scale);
        strip.Update(_app.CandleClock.UtcNow, _app.CandleClock.IsSynchronized, Settings.CandleCountdownHighlight);
        return _compactCountdown = strip;
    }
}
