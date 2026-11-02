using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Core;

internal static class MarketAutoWidthUiTests
{
    private const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static object? Field(MainWindow window, string name) => typeof(MainWindow).GetField(name, Instance)!.GetValue(window);
    private static double Wanted(MainWindow window) => (double)Field(window, "_compactWanted")!;
    private static void Refresh(MainWindow window) => typeof(MainWindow).GetMethod("UpdateMarketStrip", Instance)!.Invoke(window, null);
    private static void Market(BrimDeck.App app, MarketFeedSnapshot snapshot) => typeof(BrimDeck.App).GetProperty("Market", Instance)!.SetValue(app, snapshot);

    public static void Run(BrimDeck.App app, Action<string, bool> check)
    {
        var saved = app.Settings;
        var savedMarket = app.Market;
        var savedDeck = app.Deck;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var coins = Enumerable.Range(1, 20).Select(i => new MarketCoin("COIN" + i, "COIN" + i + "USDT", true, i, now)).ToArray();
        var snapshot = new MarketFeedSnapshot(now, coins, coins, [], null, null);
        var operations = new List<DispatcherOperation>();
        var hooks = Dispatcher.CurrentDispatcher.Hooks;
        DispatcherHookEventHandler posted = (_, e) => operations.Add(e.Operation);
        hooks.OperationPosted += posted;
        try
        {
            foreach (var style in new[] { CompactStyle.Notch, CompactStyle.Capsule })
            foreach (double dpi in new[] { 1d, 1.25, 2d })
            foreach (int scale in new[] { 75, 100, 125 })
            {
                var s = new DeckSettings { Style = style, PanelScale = scale, Apps = [], MarketRankings = [MarketRanking.Hot, MarketRanking.Gainers],
                    NotchSummary = false, CapsuleSummary = false, NotchMusic = false, CapsuleMusic = false,
                    NotchMonitor = false, CapsuleMonitor = false, NotchClock = false, CapsuleClock = false,
                    NotchEqualizer = false, CapsuleEqualizer = false, NotchCandleCountdown = false, CapsuleCandleCountdown = false,
                    NotchMarket = true, CapsuleMarket = true, NotchAutoWidth = true, CapsuleAutoWidth = true, Animations = false };
                typeof(BrimDeck.App).GetProperty(nameof(BrimDeck.App.Settings))!.SetValue(app, s);
                Market(app, snapshot);
                var window = new MainWindow(app);
                typeof(BrimDeck.App).GetProperty(nameof(BrimDeck.App.Deck))!.SetValue(app, window);
                try
                {
                    VisualTreeHelper.SetRootDpi(window, new DpiScale(dpi, dpi));
                    window.RenderCompact();
                    Layout(window, s, dpi);
                    var ticker = (MarketTickerStrip)Field(window, "_marketStrip")!;
                    var row = (StackPanel)((Canvas)ticker.Children[0]).Children[0];
                    row.Measure(new Size(double.PositiveInfinity, ticker.Height));
                    double six = row.Children.Cast<FrameworkElement>().Take(6).Sum(child => child.DesiredSize.Width);
                    double seven = six + ((FrameworkElement)row.Children[6]).DesiredSize.Width;
                    check($"{style}/{scale}%/{dpi} DPI: automatic ticker fits six complete coins and stays compact", ticker.Width >= six - 1 && ticker.Width < seven);
                    double initial = Wanted(window);
                    Market(app, snapshot with { Hot = coins.Select(c => c with { Change24h = -123.45 }).ToArray() });
                    Refresh(window);
                    check("Quote-only refresh preserves the ticker and capsule width", ReferenceEquals(ticker, Field(window, "_marketStrip")) && Wanted(window) == initial);
                    var longer = coins.Select(c => c with { Asset = "LONGTOKEN" + c.Asset }).ToArray();
                    Market(app, snapshot with { Hot = longer }); Refresh(window);
                    check("A refreshed list with longer symbols recalculates automatic width", Wanted(window) > initial + 100 * scale / 100d);
                    Market(app, snapshot with { Hot = coins.Take(2).ToArray(), Gainers = [] }); Refresh(window);
                    check("Fewer than six available coins do not leave six empty slots", Wanted(window) < initial / 2);

                    Market(app, snapshot); s.NotchMonitor = s.CapsuleMonitor = s.NotchClock = s.CapsuleClock = true;
                    s.NotchCandleCountdown = s.CapsuleCandleCountdown = true; s.CandleCountdownPeriods = CandlePeriods.All;
                    s.NotchMusic = s.CapsuleMusic = s.NotchEqualizer = s.CapsuleEqualizer = s.NotchSummary = s.CapsuleSummary = true;
                    s.Apps = [new AppEntry { QuotaSource = ProviderId.Codex, UsageSource = ProviderId.Codex, Enabled = true }];
                    s.MusicText = CompactMusicText.Title;
                    typeof(MainWindow).GetField("_mediaTrack", Instance)!.SetValue(window,
                        new MediaTrack { Id = "auto-width-test", Title = "Capsule width", State = BrimDeck.Core.MediaState.Paused });
                    window.RenderCompact(); Layout(window, s, dpi);
                    var compact = (Grid)window.FindName("Compact");
                    check("Original modules leave the automatic ticker its six-coin window", ((MarketTickerStrip)Field(window, "_marketStrip")!).Width >= six - 1);
                    foreach (string name in new[] { "_quotaCarousel", "_compactMarquee", "_compactEqualizer", "_compactStrip", "_compactClock", "_compactCountdown", "_marketStrip" })
                    {
                        var child = (FrameworkElement)Field(window, name)!;
                        double left = child.TransformToAncestor(compact).Transform(new Point()).X;
                        double right = child.TransformToAncestor(compact).Transform(new Point(child.ActualWidth, 0)).X;
                        check($"{name} remains visible within the automatic row", child.ActualWidth > 0 && left >= -1 && right <= compact.Width + 1);
                    }
                    Layout(window, s, dpi, 800);
                    check("Automatic capsule stays within a narrow screen", window.Collapsed.Surface.Width <= 800 && window.CurrentCompactLayout!.MarketWidth > 0);
                    s.NotchAutoWidth = s.CapsuleAutoWidth = false; window.RenderCompact();
                    double manual = Wanted(window); var manualTicker = Field(window, "_marketStrip");
                    Market(app, snapshot with { Hot = longer }); Refresh(window);
                    check("A manual capsule width is not refitted on ranking refresh", Wanted(window) == manual && ReferenceEquals(manualTicker, Field(window, "_marketStrip")));
                }
                finally { typeof(MainWindow).GetField("_retiring", Instance)!.SetValue(window, true); window.Close(); }
            }
        }
        finally
        {
            hooks.OperationPosted -= posted;
            foreach (var operation in operations) operation.Abort();
            typeof(BrimDeck.App).GetProperty(nameof(BrimDeck.App.Settings))!.SetValue(app, saved);
            typeof(BrimDeck.App).GetProperty(nameof(BrimDeck.App.Deck))!.SetValue(app, savedDeck);
            Market(app, savedMarket);
        }
    }

    private static void Layout(MainWindow window, DeckSettings settings, double dpi, double width = 3000)
    {
        var monitor = new DipRect(0, 0, width, 1500);
        var shape = CollapsedGeometry.Resolve(settings, settings.Style, monitor, monitor, dpi, Wanted(window),
            (double)Field(window, "_compactRequired")!, window.Scale);
        typeof(MainWindow).GetProperty(nameof(MainWindow.Collapsed), Instance)!.SetValue(window, shape);
        window.RenderCompact();
        var compact = (Grid)window.FindName("Compact");
        compact.Measure(new Size(compact.Width, compact.Height)); compact.Arrange(new Rect(0, 0, compact.Width, compact.Height)); compact.UpdateLayout();
    }
}
