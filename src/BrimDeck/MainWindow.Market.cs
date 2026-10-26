using System.Windows;
using System.Windows.Controls;
using System.IO;
using BrimDeck.Core;

namespace BrimDeck;

public partial class MainWindow
{
    private MarketTickerStrip? _marketStrip;
    private TimeSpan _marketElapsed;
    private MarketRanking? _marketRankingRendered;
    private MarketPageView? _marketPageView;
    private bool IsMarketPage => CurrentPage == DeckPage.Market;
    private bool MarketSummaryActive => EffectiveStyle != CompactStyle.Line && Settings.ShowsMarket(EffectiveStyle);
    internal bool MarketDemand => IsVisible && !IsSuppressed && (_expanded ? IsMarketPage : Settings.ShowsMarket(EffectiveStyle));
    private bool MarketHoverTarget => Settings.ShowsMarket(EffectiveStyle) && (EffectiveStyle == CompactStyle.Line || _marketStrip?.IsMouseOver == true);
    private void InitializeMarket()
    {
        _app.MarketUpdated += UpdateMarketStrip;
        Closed += (_, _) => { _app.MarketUpdated -= UpdateMarketStrip; _marketStrip?.Stop(); };
    }
    private void UpdateMarketStrip()
    {
        if (MarketSummaryActive) _marketStrip?.Update(_app.Market, Settings.MarketRanking, _app.MarketError);
        _marketPageView?.Update(_app.Market, Settings.MarketRanking, _app.MarketError);
        UpdateMarketIndicator();
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
    private void MarketPage_Click(object sender, RoutedEventArgs e) => SelectPage(DeckPage.Market);
    private void RenderMarket()
    {
        RefreshStatus();
        UsageContent.Visibility = MusicContent.Visibility = MonitorContent.Visibility = Visibility.Collapsed;
        MarketContent.Visibility = Visibility.Visible;
        ModelToolbar.Visibility = ApplicationPages.Visibility = Visibility.Collapsed; ClockLabel.Visibility = Visibility.Visible;
        ApplyHeader(PanelHeader.For(36, 14)); MarketContent.Margin = new Thickness(14, 0, 14, 12);
        if (_marketPageView is null)
        {
            _marketPageView = new MarketPageView(ranking =>
            {
                Settings.MarketRanking = ranking; _app.QueueSave();
                foreach (var deck in _app.Decks) { deck.RenderCompact(); deck.UpdateMarketStrip(); }
            }, ImportMarketConfiguration);
            MarketContent.Children.Add(_marketPageView);
        }
        _marketPageView.Update(_app.Market, Settings.MarketRanking, _app.MarketError);
    }
    private async void ImportMarketConfiguration()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Market feed configuration|*.json", FileName = "market-feed.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var configuration = System.Text.Json.JsonSerializer.Deserialize<MarketFeedConfiguration>(File.ReadAllText(dialog.FileName),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (configuration is null || !MarketFeedConfiguration.ValidEndpoint(configuration.Endpoint, configuration.AllowPrivateHttp) || string.IsNullOrWhiteSpace(configuration.Token)) throw new InvalidDataException();
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "market-feed.json"), System.Text.Json.JsonSerializer.Serialize(configuration));
            await _app.RefreshMarketAsync();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        { _marketPageView?.Update(_app.Market, Settings.MarketRanking, Loc.T("无法导入行情配置，请检查文件和目录权限", "Cannot import the feed configuration; check the file and folder permissions")); }
    }
    private void UpdateMarketIndicator()
    {
        if (MarketIndicator is null) return;
        bool shown = !_expanded && EffectiveStyle == CompactStyle.Line && Settings.LineMarket && _alert is null;
        MarketIndicator.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (!shown) { Island.ToolTip = null; return; }
        var coin = _app.Market.Rows(Settings.MarketRanking).FirstOrDefault();
        var change = coin?.CurrentChange(DateTimeOffset.UtcNow);
        MarketIndicator.Height = Collapsed.Surface.Height; MarketIndicator.Width = Math.Min(6, Collapsed.Surface.Width / 4);
        MarketIndicator.Background = UI.Brush(_app.MarketError is not null ? "#FBBF24" : change is > 0 ? "#36D399" : change is < 0 ? "#FB7185" : UI.Tertiary);
        Island.ToolTip = MarketPageView.Label(Settings.MarketRanking) + " · " + (change is { } percent ? $"{coin!.Asset} {percent:+0.00;-0.00;0.00}%" : Loc.T("等待有效行情", "Waiting for current quotes"))
            + Loc.T(" · 悬停查看完整榜单", " · hover for the full ranking");
    }
    internal string RuntimeAppearanceDescription
    {
        get
        {
            static string Name(CompactStyle style) => style switch { CompactStyle.Notch => Loc.T("刘海", "Notch"), CompactStyle.Capsule => Loc.T("胶囊", "Capsule"), _ => Loc.T("指标条", "Indicator") };
            string reason = IsSuppressed ? Loc.T("（当前窗口规则隐藏）", " (hidden by window rules)") : EffectiveStyle != Settings.Style ? Loc.T("（窗口规则生效）", " (window rule active)") : "";
            string room = EffectiveStyle != CompactStyle.Line && Collapsed.ContentWidth + 1 < _compactWanted ? Loc.T(" · 当前宽度不足，可启用自动宽度", " · width is limited; enable automatic width") : "";
            return Loc.T("选择外观：", "Selected: ") + Name(Settings.Style) + Loc.T(" · 当前生效：", " · active: ") + Name(EffectiveStyle) + reason + room;
        }
    }
}
