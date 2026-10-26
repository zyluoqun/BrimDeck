using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BrimDeck;
using BrimDeck.Core;

internal static class Program
{
    private static int _checks;
    private static void Check(string name, bool condition)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
        _checks++; Console.WriteLine("PASS: " + name);
    }
    // Builds and measures the real controls without showing windows, starting the app or reading accounts.
    [STAThread]
    private static int Main()
    {
        try
        {
            var app = new BrimDeck.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent(); PanelTheme.Use(app.Settings);
            foreach (var style in new[] { CompactStyle.Notch, CompactStyle.Capsule })
            {
                var s = app.Settings; s.Style = style; s.NotchSummary = s.CapsuleSummary = false; s.NotchMusic = s.CapsuleMusic = false;
                s.NotchClock = s.CapsuleClock = false; s.NotchMonitor = s.CapsuleMonitor = true;
                s.NotchMarket = s.CapsuleMarket = false; s.NotchWidth = s.CapsuleWidth = 640; s.NotchAutoWidth = s.CapsuleAutoWidth = false;
                s.Normalize();
                var window = new MainWindow(app);
                typeof(BrimDeck.App).GetProperty(nameof(BrimDeck.App.Deck))!.SetValue(app, window);
                try
                {
                    window.RenderCompact();
                    var monitor = (FrameworkElement?)typeof(MainWindow).GetField("_compactStrip", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window);
                    Check($"{style}: monitoring alone produces a real monitor control with width", monitor is not null && monitor.Width > 0 && window.CurrentCompactLayout!.MonitorWidth > 0);
                    s.NotchMarket = s.CapsuleMarket = true; window.RenderCompact();
                    monitor = (FrameworkElement?)typeof(MainWindow).GetField("_compactStrip", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window);
                    Check($"{style}: actual market and monitoring controls coexist", monitor is not null && monitor.Width > 0 && window.CurrentCompactLayout!.MarketWidth > 0
                        && typeof(MainWindow).GetField("_marketStrip", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window) is MarketTickerStrip);
                    window.PointerChanged(true);
                    var hover = (PromptTimer)typeof(MainWindow).GetField("_hover", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                    Check($"{style}: enabling market keeps the original hover timer", hover.IsEnabled); window.PointerChanged(false);
                    Check("The real panel contains all four module buttons", new[] { "AIUsageButton", "MusicPageButton", "MonitorPageButton", "MarketPageButton" }.All(name => window.FindName(name) is Button));
                    CheckMarketList(window);
                }
                finally { typeof(MainWindow).GetField("_retiring", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true); window.Close(); }
            }
            app.Settings.Style = CompactStyle.Line; app.Settings.LineMarket = true;
            var line = new MainWindow(app); typeof(BrimDeck.App).GetProperty(nameof(BrimDeck.App.Deck))!.SetValue(app, line);
            try
            {
                line.RenderCompact(); var marker = (Border)line.FindName("MarketIndicator");
                Check("Market indicator retains the thin original line height", marker.Visibility == Visibility.Visible && marker.Height == app.Settings.LineThickness);
                Check("Original music progress indicator remains available", line.FindName("MusicIndicator") is Border);
            }
            finally { typeof(MainWindow).GetField("_retiring", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(line, true); line.Close(); }
            Console.WriteLine($"{_checks} product UI checks passed."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void CheckMarketList(MainWindow window)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var rows = Enumerable.Range(1, 30).Select(n => new MarketCoin("COIN" + n, "COIN" + n + "USDT", true, n, now)).ToArray();
        var snapshot = new MarketFeedSnapshot(now, rows, rows.Take(12).ToArray(), rows.Take(10).ToArray(), null, null);
        var view = new MarketPageView(_ => { }, () => { });
        ((Grid)window.FindName("MarketContent")).Children.Add(view);
        view.Update(snapshot, MarketRanking.Hot, null); view.Measure(new Size(600, 230)); view.Arrange(new Rect(0, 0, 600, 230)); view.UpdateLayout();
        Check("Market page renders the entire ranking", view.RowCount == 30 && view.ListScroll.ScrollableHeight > 0);
        view.ListScroll.ScrollToVerticalOffset(120); view.UpdateLayout(); double position = view.ListScroll.VerticalOffset;
        view.Update(snapshot, MarketRanking.Hot, null); view.UpdateLayout();
        Check("Quote refresh preserves the list scroll position", position > 0 && Math.Abs(view.ListScroll.VerticalOffset - position) < .01);
        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
        view.ListScroll.RaiseEvent(wheel);
        Check("The ranking list consumes its wheel without switching modules", wheel.Handled);
        view.Update(snapshot, MarketRanking.Losers, null);
        Check("Board selection replaces the full list", view.RowCount == 10 && view.Ranking == MarketRanking.Losers);
        view.Update(snapshot, MarketRanking.Hot, "offline");
        Check("Offline market data has an explicit empty error state", view.RowCount == 0);
    }
}
