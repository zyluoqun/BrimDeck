using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Documents;
using BrimDeck.Core;

namespace BrimDeck;

// Keeps the list and its scroll position while quotes change; only a different board rebuilds the rows.
internal sealed class MarketPageView : Grid
{
    private readonly StackPanel _rows = new();
    private readonly TextBlock _status = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _empty = new() { FontSize = 13, Margin = new Thickness(0, 18, 0, 0) };
    private readonly Border _countdownHost = new() { Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed };
    private CandleCountdownStrip? _countdown;
    private readonly Dictionary<MarketRanking, Button> _tabs = [];
    private readonly List<(MarketCoin Coin, TextBlock Change, MarketLoadingRing Ring)> _values = [];
    private HashSet<(string Symbol, bool Perpetual)> _pending = [];
    private readonly Action<MarketRanking> _select;
    private readonly Func<MarketCoin, Task>? _switchChart;
    private string _key = "";
    internal ScrollViewer ListScroll { get; }
    internal int RowCount => _values.Count;
    internal MarketRanking Ranking { get; private set; }
    internal static double PreferredWidth(MarketFeedSnapshot snapshot, MarketRanking ranking)
    {
        static double TextWidth(string text, double size = 12)
        {
            var label = new TextBlock { Text = text, FontSize = size };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); return label.DesiredSize.Width;
        }
        double toolbar = MarketBoards.Order.Sum(value => Math.Max(64, TextWidth(Label(value)) + 20) + 8)
            + Math.Max(64, TextWidth(Loc.T("导入配置", "Import feed")) + 20);
        double symbol = snapshot.Rows(ranking).Select(coin => TextWidth(coin.Asset + (coin.Perpetual ? Loc.T("  合约", "  perpetual") : Loc.T("  现货", "  spot"))))
            .DefaultIfEmpty(80).Max();
        // Outer margins, row padding, rank, percent and scrollbar; timestamps wrap instead of widening the panel.
        return Math.Clamp(Math.Ceiling(Math.Max(toolbar + 28, symbol + 30 + 90 + 20 + 19 + 28)), 360, DeckPages.MaxWidth);
    }
    public MarketPageView(Action<MarketRanking> select, Action import, Func<MarketCoin, Task>? switchChart = null)
    {
        _select = select; _switchChart = switchChart;
        RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new());
        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var configure = TextButton(Loc.T("导入配置", "Import feed"));
        configure.ToolTip = Loc.T("导入行情配置", "Import feed configuration");
        configure.Click += (_, _) => import(); DockPanel.SetDock(configure, Dock.Right); toolbar.Children.Add(configure);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var ranking in MarketBoards.Order)
        {
            var button = TextButton(Label(ranking)); button.Margin = new Thickness(0, 0, 8, 0);
            button.Click += (_, _) => _select(ranking); _tabs[ranking] = button; tabs.Children.Add(button);
        }
        toolbar.Children.Add(tabs); Children.Add(toolbar);
        SetRow(_countdownHost, 1); Children.Add(_countdownHost);
        _status.Margin = new Thickness(0, 0, 0, 8); SetRow(_status, 2); Children.Add(_status);
        ListScroll = new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch, CanContentScroll = false, Focusable = false };
        ListScroll.PreviewMouseWheel += (_, e) => { if (_pending.Count == 0) ListScroll.ScrollToVerticalOffset(ListScroll.VerticalOffset - e.Delta / 3d); e.Handled = true; };
        SetRow(ListScroll, 3); Children.Add(ListScroll); SetRow(_empty, 3); Children.Add(_empty);
    }
    internal void UpdateCountdown(DeckSettings settings, DateTimeOffset now, bool synchronized)
    {
        _countdownHost.Visibility = settings.CandleCountdownWanted ? Visibility.Visible : Visibility.Collapsed;
        if (!settings.CandleCountdownWanted) return;
        if (_countdown is null || _countdown.Periods != settings.CandleCountdownPeriods)
            _countdownHost.Child = _countdown = new CandleCountdownStrip(settings.CandleCountdownPeriods);
        _countdown.Update(now, synchronized, settings.CandleCountdownHighlight);
    }
    internal static string Label(MarketRanking ranking) => ranking switch
    { MarketRanking.Pool => Loc.T("观察池", "Watch pool"), MarketRanking.Gainers => Loc.T("涨幅榜", "Gainers"), MarketRanking.Losers => Loc.T("跌幅榜", "Losers"), _ => Loc.T("热门", "Hot") };
    private static Button TextButton(string text)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 4, 10, 4), MinWidth = 64, Height = 28, Foreground = UI.Brush(UI.Primary) };
        button.SetResourceReference(StyleProperty, "PanelTextButton"); return button;
    }
    public void Update(MarketFeedSnapshot snapshot, MarketRanking ranking, string? error, string? chartStatus = null, int count = 50)
    {
        Ranking = ranking;
        foreach (var (key, tab) in _tabs) { tab.Foreground = UI.Brush(key == ranking ? UI.Accent : UI.Secondary); tab.FontWeight = key == ranking ? FontWeights.SemiBold : FontWeights.Normal; }
        var problem = error ?? snapshot.BoardError(ranking);
        IReadOnlyList<MarketCoin> rows = problem is null ? snapshot.Rows(ranking).Take(Math.Clamp(count, 1, 50)).ToArray() : [];
        string keyNow = ranking + "|" + string.Join('|', rows.Select(coin => coin.Symbol + ":" + coin.Perpetual));
        if (_key != keyNow && _pending.Count == 0)
        {
            _key = keyNow; _rows.Children.Clear(); _values.Clear(); ListScroll.ScrollToTop();
            for (int index = 0; index < rows.Count; index++) AddRow(rows[index], index + 1);
        }
        _empty.Text = problem ?? (ranking == MarketRanking.Pool ? Loc.T("观察池暂无币种", "The observation pool is empty") : Loc.T("等待服务端采集榜单", "Waiting for the server ranking snapshot"));
        _empty.Foreground = UI.Brush(UI.Secondary); _empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var now = DateTimeOffset.UtcNow;
        var quotes = rows.ToDictionary(coin => (coin.Symbol, coin.Perpetual));
        foreach (var (coin, change, _) in _values)
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
            + (waiting > 0 ? Loc.T($" · {waiting} 项等待有效行情", $" · {waiting} awaiting current quotes") : "")
            + (chartStatus is null ? "" : "\n" + chartStatus);
        _status.Foreground = UI.Brush(UI.Tertiary);
    }
    private void AddRow(MarketCoin coin, int rank)
    {
        var row = new Grid(); row.ColumnDefinitions.Add(new() { Width = new GridLength(30) }); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = new GridLength(90) });
        var number = new TextBlock { Text = rank.ToString(), FontSize = 11, Foreground = UI.Brush(UI.Tertiary), VerticalAlignment = VerticalAlignment.Center }; row.Children.Add(number);
        var symbol = new TextBlock { FontSize = 12,
            Foreground = UI.Brush(UI.Primary), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var ring = new MarketLoadingRing(); ring.SetLoading(_pending.Contains((coin.Symbol, coin.Perpetual)));
        symbol.Inlines.Add(new InlineUIContainer(ring) { BaselineAlignment = BaselineAlignment.Center }); symbol.Inlines.Add(new Run(coin.Asset + "  "));
        symbol.Inlines.Add(new Run(coin.Perpetual ? Loc.T("合约", "perpetual") : Loc.T("现货", "spot")));
        SetColumn(symbol, 1); row.Children.Add(symbol);
        var change = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        SetColumn(change, 2); row.Children.Add(change);
        var button = TextButton(""); button.Content = row; button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.Height = 32; button.Margin = new Thickness(0, 0, 2, 2); button.ToolTip = coin.Symbol;
        button.SetResourceReference(StyleProperty, "MarketRowButton");
        System.Windows.Automation.AutomationProperties.SetName(button, coin.Asset + Loc.T("，打开图表", ", open chart"));
        ToolTipService.SetIsEnabled(button, false);
        button.Click += async (_, _) => { if (_switchChart is not null) await _switchChart(coin); };
        _values.Add((coin, change, ring)); _rows.Children.Add(button);
    }
    internal void SetPending(IEnumerable<MarketCoin> coins)
    {
        _pending = coins.Select(coin => (coin.Symbol, coin.Perpetual)).ToHashSet();
        foreach (var (coin, _, ring) in _values) ring.SetLoading(_pending.Contains((coin.Symbol, coin.Perpetual)));
    }
}
