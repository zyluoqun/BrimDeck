using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BrimDeck.Core;

namespace BrimDeck;

// Keeps the list and its scroll position while quotes change; only a different board rebuilds the rows.
internal sealed class MarketPageView : Grid
{
    private readonly StackPanel _rows = new();
    private readonly TextBlock _status = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _empty = new() { FontSize = 13, Margin = new Thickness(0, 18, 0, 0) };
    private readonly Dictionary<MarketRanking, Button> _tabs = [];
    private readonly List<(MarketCoin Coin, TextBlock Change)> _values = [];
    private readonly Action<MarketRanking> _select;
    private string _key = "";
    internal ScrollViewer ListScroll { get; }
    internal int RowCount => _values.Count;
    internal MarketRanking Ranking { get; private set; }
    public MarketPageView(Action<MarketRanking> select, Action import)
    {
        _select = select;
        RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new());
        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var configure = TextButton(Loc.T("导入行情配置", "Import feed configuration"));
        configure.Click += (_, _) => import(); DockPanel.SetDock(configure, Dock.Right); toolbar.Children.Add(configure);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var ranking in Enum.GetValues<MarketRanking>())
        {
            var button = TextButton(Label(ranking)); button.Margin = new Thickness(0, 0, 8, 0);
            button.Click += (_, _) => _select(ranking); _tabs[ranking] = button; tabs.Children.Add(button);
        }
        toolbar.Children.Add(tabs); Children.Add(toolbar);
        _status.Margin = new Thickness(0, 0, 0, 8); SetRow(_status, 1); Children.Add(_status);
        ListScroll = new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch, CanContentScroll = false, Focusable = false };
        ListScroll.PreviewMouseWheel += (_, e) => { ListScroll.ScrollToVerticalOffset(ListScroll.VerticalOffset - e.Delta / 3d); e.Handled = true; };
        SetRow(ListScroll, 2); Children.Add(ListScroll); SetRow(_empty, 2); Children.Add(_empty);
    }
    internal static string Label(MarketRanking ranking) => ranking switch
    { MarketRanking.Gainers => Loc.T("涨幅榜", "Gainers"), MarketRanking.Losers => Loc.T("跌幅榜", "Losers"), _ => Loc.T("热门", "Hot") };
    private static Button TextButton(string text)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 4, 10, 4), MinWidth = 64, Height = 28, Foreground = UI.Brush(UI.Primary) };
        button.SetResourceReference(StyleProperty, "PanelTextButton"); return button;
    }
    public void Update(MarketFeedSnapshot snapshot, MarketRanking ranking, string? error)
    {
        Ranking = ranking;
        foreach (var (key, tab) in _tabs) { tab.Foreground = UI.Brush(key == ranking ? UI.Accent : UI.Secondary); tab.FontWeight = key == ranking ? FontWeights.SemiBold : FontWeights.Normal; }
        var problem = error ?? snapshot.Error ?? (ranking == MarketRanking.Hot ? snapshot.HotError : null);
        var rows = problem is null ? snapshot.Rows(ranking) : [];
        string keyNow = ranking + "|" + string.Join('|', rows.Select(coin => coin.Symbol + ":" + coin.Perpetual));
        if (_key != keyNow)
        {
            _key = keyNow; _rows.Children.Clear(); _values.Clear(); ListScroll.ScrollToTop();
            for (int index = 0; index < rows.Count; index++) AddRow(rows[index], index + 1);
        }
        _empty.Text = problem ?? Loc.T("等待服务端采集榜单", "Waiting for the server ranking snapshot");
        _empty.Foreground = UI.Brush(UI.Secondary); _empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var now = DateTimeOffset.UtcNow;
        var quotes = rows.ToDictionary(coin => (coin.Symbol, coin.Perpetual));
        foreach (var (coin, change) in _values)
        {
            var value = quotes.GetValueOrDefault((coin.Symbol, coin.Perpetual))?.CurrentChange(now);
            change.Text = value is { } percent ? percent.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + "%" : "—";
            change.Foreground = UI.Brush(value is > 0 ? "#36D399" : value is < 0 ? "#FB7185" : UI.Secondary);
        }
        string collected = snapshot.SnapshotAt is { } at ? DateTimeOffset.FromUnixTimeMilliseconds(at).ToLocalTime().ToString("MM-dd HH:mm:ss") : "—";
        long latest = rows.Select(coin => coin.QuoteAt ?? 0).DefaultIfEmpty().Max();
        string quoted = latest > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(latest).ToLocalTime().ToString("HH:mm:ss") : "—";
        int waiting = rows.Count(coin => coin.CurrentChange(now) is null);
        _status.Text = Loc.T($"名单更新 {collected} · 报价更新 {quoted} · 共 {rows.Count} 项", $"Ranking {collected} · quotes {quoted} · {rows.Count} coins")
            + (waiting > 0 ? Loc.T($" · {waiting} 项等待有效行情", $" · {waiting} awaiting current quotes") : "");
        _status.Foreground = UI.Brush(UI.Tertiary);
    }
    private void AddRow(MarketCoin coin, int rank)
    {
        var row = new Grid(); row.ColumnDefinitions.Add(new() { Width = new GridLength(30) }); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = new GridLength(90) });
        var number = new TextBlock { Text = rank.ToString(), FontSize = 11, Foreground = UI.Brush(UI.Tertiary), VerticalAlignment = VerticalAlignment.Center }; row.Children.Add(number);
        var symbol = new TextBlock { Text = coin.Asset + (coin.Perpetual ? Loc.T("  合约", "  perpetual") : Loc.T("  现货", "  spot")), FontSize = 12,
            Foreground = UI.Brush(UI.Primary), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        SetColumn(symbol, 1); row.Children.Add(symbol);
        var change = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        SetColumn(change, 2); row.Children.Add(change);
        var button = TextButton(""); button.Content = row; button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.Height = 32; button.Margin = new Thickness(0, 0, 2, 2); button.ToolTip = coin.Symbol;
        button.SetResourceReference(StyleProperty, "MarketRowButton");
        System.Windows.Automation.AutomationProperties.SetName(button, coin.Asset + Loc.T("，打开图表", ", open chart"));
        button.Click += (_, _) => { try { UI.OpenUrl(MarketCoin.ChartUrl(coin.Symbol, coin.Perpetual)); } catch (System.ComponentModel.Win32Exception) { button.ToolTip = Loc.T("无法打开浏览器", "Cannot open the browser"); } };
        _values.Add((coin, change)); _rows.Children.Add(button);
    }
}
