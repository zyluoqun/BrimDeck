using System.Windows;
using System.Windows.Controls;
using BrimDeck.Core;

namespace BrimDeck;

public partial class MainWindow
{
    private MarketTickerStrip? _marketStrip;
    private TimeSpan _marketElapsed;
    private MarketRanking? _marketRankingRendered;
    private bool MarketCapsuleActive => EffectiveStyle == CompactStyle.Capsule && Settings.CapsuleMarket;
    private void InitializeMarket()
    {
        _app.MarketUpdated += UpdateMarketStrip;
        Closed += (_, _) => { _app.MarketUpdated -= UpdateMarketStrip; _marketStrip?.Stop(); };
    }
    private void UpdateMarketStrip()
    {
        if (MarketCapsuleActive) _marketStrip?.Update(_app.Market, Settings.MarketRanking, _app.MarketError);
    }
    private MarketTickerStrip CreateMarketStrip(double width, double rowHeight, double scale)
    {
        if (_marketRankingRendered != Settings.MarketRanking) _marketElapsed = TimeSpan.Zero;
        _marketRankingRendered = Settings.MarketRanking;
        _marketStrip = new MarketTickerStrip(width, rowHeight, scale,
            Settings.Animations && SystemParameters.ClientAreaAnimation, _marketElapsed);
        var menu = new ContextMenu();
        var settings = new MenuItem { Header = Loc.T("设置", "Settings") };
        settings.Click += (_, _) => _app.OpenSettings(); menu.Items.Add(settings); _marketStrip.ContextMenu = menu;
        _marketStrip.Update(_app.Market, Settings.MarketRanking, _app.MarketError);
        return _marketStrip;
    }
}
