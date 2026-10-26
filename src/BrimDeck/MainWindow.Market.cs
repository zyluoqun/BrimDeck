using System.Windows;
using System.Windows.Controls;
using BrimDeck.Core;

namespace BrimDeck;

public partial class MainWindow
{
    private MarketTickerStrip? _marketStrip;
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
    private void RenderMarketStrip()
    {
        var shape = Collapsed; _compactRendered = shape;
        double scale = CompactMusicLayout.SafeScale(shape.Scale);
        _marketStrip = new MarketTickerStrip(CompactContentWidth(shape), Math.Max(1, 26 * scale), scale,
            Settings.Animations && SystemParameters.ClientAreaAnimation);
        var menu = new ContextMenu();
        var settings = new MenuItem { Header = Loc.T("设置", "Settings") };
        settings.Click += (_, _) => _app.OpenSettings(); menu.Items.Add(settings); _marketStrip.ContextMenu = menu;
        _marketStrip.Update(_app.Market, Settings.MarketRanking, _app.MarketError);
        CompactRings.Children.Add(_marketStrip);
        _compactWanted = 660;
        UpdateMonitorDemand(); UpdateMusicTimer();
    }
}
