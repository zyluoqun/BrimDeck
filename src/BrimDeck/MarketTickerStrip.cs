using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck;

// Updating a quote keeps the buttons and scrolling clock in place, so a refresh never restarts the ticker.
internal sealed class MarketTickerStrip : Grid
{
    private readonly double _scale;
    private readonly bool _animations;
    private readonly Canvas _viewport;
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal };
    private readonly TranslateTransform _shift = new();
    private readonly List<(MarketCoin Coin, Run Value, Button Button, MarketLoadingRing Ring)> _values = [];
    private HashSet<(string Symbol, bool Perpetual)> _pending = [];
    private string? _error;
    private AnimationClock? _clock;
    private readonly DispatcherTimer _stale = new() { Interval = TimeSpan.FromSeconds(5) };
    private MarketFeedSnapshot _snapshot = MarketFeedSnapshot.Empty;
    private MarketRanking _ranking;
    private string _key = "";
    private TimeSpan _elapsed;
    private bool _running = true, _pointerPause;
    private readonly Func<MarketCoin, Task>? _switchChart;
    public TimeSpan Elapsed => _clock?.CurrentTime ?? _elapsed;
    public MarketTickerStrip(double width, double height, double scale, bool animations, TimeSpan elapsed = default, Func<MarketCoin, Task>? switchChart = null)
    {
        _switchChart = switchChart; ToolTipService.SetIsEnabled(this, false);
        Width = Math.Max(0, width); Height = height; _scale = scale; _animations = animations; _elapsed = elapsed;
        _viewport = new Canvas { Width = Width, Height = Height, ClipToBounds = true, Background = Brushes.Transparent };
        _row.RenderTransform = _shift; _viewport.Children.Add(_row); Children.Add(_viewport);
        MouseEnter += (_, _) => ApplyRunning();
        MouseLeave += (_, _) => ApplyRunning();
        _stale.Tick += (_, _) => PaintValues();
        Loaded += (_, _) => ApplyRunning();
        IsVisibleChanged += (_, _) => ApplyRunning();
        Unloaded += (_, _) => Stop();
    }
    public void Stop() { _elapsed = Elapsed; _stale.Stop(); _clock?.Controller?.Remove(); _clock = null; }
    public void SetRunning(bool running, bool pointerInside) { _running = running; _pointerPause = pointerInside; ApplyRunning(); }
    private void ApplyRunning()
    {
        if (_running && IsVisible)
        {
            _stale.Start();
            if (IsMouseOver || _pointerPause || _pending.Count > 0) _clock?.Controller?.Pause(); else _clock?.Controller?.Resume();
        }
        else { _stale.Stop(); _clock?.Controller?.Pause(); }
    }
    public void Update(MarketFeedSnapshot snapshot, MarketRanking ranking, string? error)
    {
        _snapshot = snapshot; _ranking = ranking; _error = error;
        var rows = snapshot.Rows(ranking);
        var problem = error ?? (ranking == MarketRanking.Hot ? snapshot.HotError : null);
        if (problem is not null) rows = [];
        string key = ranking + "|" + string.Join("|", rows.Select(row => $"{row.Symbol}:{row.Perpetual}")) + "|" + problem;
        if (_key != key && _pending.Count == 0)
        {
            if (_key.Length > 0) _elapsed = TimeSpan.Zero;
            _key = key; _clock?.Controller?.Remove(); _clock = null; _shift.X = 0;
            _viewport.MouseWheel -= Wheel;
            _row.Children.Clear(); _values.Clear();
            if (rows.Count == 0)
            {
                _row.Children.Add(new TextBlock { Text = problem is null ? Loc.T("等待榜单行情…", "Waiting for market data…") : Loc.T("行情暂不可用", "Market data unavailable"),
                    Foreground = UI.Brush(UI.Secondary), FontSize = 12 * _scale, Margin = new Thickness(8 * _scale, 4 * _scale, 0, 0) });
            }
            else
            {
                for (int i = 0; i < rows.Count; i++) AddCoin(rows[i], i + 1);
                _row.Measure(new Size(double.PositiveInfinity, Height));
                double cycle = _row.DesiredSize.Width;
                if (cycle > Width && _animations)
                {
                    for (int i = 0; i < rows.Count; i++) AddCoin(rows[i], i + 1);
                    var motion = new DoubleAnimation(0, -cycle, TimeSpan.FromSeconds(cycle / (28 * _scale))) { RepeatBehavior = RepeatBehavior.Forever };
                    _clock = motion.CreateClock(); _shift.ApplyAnimationClock(TranslateTransform.XProperty, _clock);
                    if (_elapsed > TimeSpan.Zero) _clock.Controller?.Seek(TimeSpan.FromTicks(_elapsed.Ticks % motion.Duration.TimeSpan.Ticks), TimeSeekOrigin.BeginTime);
                }
                else if (cycle > Width)
                {
                    // Reduced motion: the wheel scrolls the full list without automatic movement.
                    _viewport.MouseWheel += Wheel;
                }
            }
        }
        ToolTip = problem ?? (snapshot.SnapshotAt is { } at ? Loc.T("24H 涨跌幅 · 名单采集 ", "24h change · ranking collected ") + DateTimeOffset.FromUnixTimeMilliseconds(at).ToLocalTime().ToString("MM-dd HH:mm:ss") : null);
        PaintValues();
        ApplyRunning();
    }
    private void Wheel(object sender, MouseWheelEventArgs e)
    {
        if (_pending.Count > 0) { e.Handled = true; return; }
        _row.Measure(new Size(double.PositiveInfinity, Height));
        _shift.X = Math.Clamp(_shift.X + (e.Delta > 0 ? 80 : -80), -Math.Max(0, _row.DesiredSize.Width - Width), 0); e.Handled = true;
    }
    private void AddCoin(MarketCoin coin, int rank)
    {
        var change = coin.CurrentChange(DateTimeOffset.UtcNow);
        var value = new Run(change is { } percent ? percent.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + "%" : "—");
        var text = new TextBlock { FontSize = 12 * _scale, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"), Foreground = UI.Brush(UI.Primary) };
        var ring = new MarketLoadingRing(_scale); ring.SetLoading(_pending.Contains((coin.Symbol, coin.Perpetual)));
        text.Inlines.Add(new InlineUIContainer(ring) { BaselineAlignment = BaselineAlignment.Center });
        text.Inlines.Add(new Run(rank.ToString(CultureInfo.InvariantCulture) + ".") { Foreground = UI.Brush(UI.Secondary), FontWeight = FontWeights.Normal });
        text.Inlines.Add(new Run(coin.Asset + " ")); text.Inlines.Add(value);
        // Reserve the percentage before measuring the animation cycle, including while quotes are unavailable.
        var measure = new TextBlock { Text = rank + "." + coin.Asset + " +0000.00%", FontSize = text.FontSize, FontWeight = text.FontWeight, FontFamily = text.FontFamily };
        measure.Measure(new Size(double.PositiveInfinity, Height));
        text.MinWidth = Math.Ceiling(measure.DesiredSize.Width) + 15 * _scale;
        var template = new ControlTemplate(typeof(Button));
        var content = new FrameworkElementFactory(typeof(ContentPresenter)); template.VisualTree = content;
        var button = new Button { Content = text, Template = template, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(0), Margin = new Thickness(8 * _scale, 0, 8 * _scale, 0), Height = Height,
            Cursor = Cursors.Hand, FocusVisualStyle = null };
        System.Windows.Automation.AutomationProperties.SetName(button, coin.Asset + Loc.T("，打开图表", ", open chart"));
        ToolTipService.SetIsEnabled(button, false);
        button.Click += async (_, _) => { if (_switchChart is not null) await _switchChart(coin); };
        _values.Add((coin, value, button, ring)); _row.Children.Add(button);
    }
    internal void SetPending(IEnumerable<MarketCoin> coins)
    {
        bool wasPending = _pending.Count > 0;
        _pending = coins.Select(coin => (coin.Symbol, coin.Perpetual)).ToHashSet();
        foreach (var (coin, _, _, ring) in _values) ring.SetLoading(_pending.Contains((coin.Symbol, coin.Perpetual)));
        if (wasPending && _pending.Count == 0) Update(_snapshot, _ranking, _error);
        ApplyRunning();
    }
    private void PaintValues()
    {
        var current = _snapshot.Rows(_ranking).ToDictionary(row => (row.Symbol, row.Perpetual));
        foreach (var (coin, value, button, _) in _values)
        {
            var change = current.GetValueOrDefault((coin.Symbol, coin.Perpetual))?.CurrentChange(DateTimeOffset.UtcNow);
            value.Text = change is { } percent ? percent.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + "%" : "—";
            value.Foreground = UI.Brush(change is > 0 ? "#36D399" : change is < 0 ? "#FB7185" : UI.Secondary);
            button.ToolTip = coin.Symbol + (coin.Perpetual ? Loc.T(" · 永续合约", " · perpetual") : Loc.T(" · 现货", " · spot"))
                + (change is null ? Loc.T(" · 等待有效行情", " · waiting for a current quote") : " · 24H " + value.Text);
        }
    }
}
