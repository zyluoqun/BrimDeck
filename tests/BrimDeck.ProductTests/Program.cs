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
            app.Updates = new AppUpdates(BrimDeck.Updates.GitHubUpdates.Create(AppContext.BaseDirectory), AppContext.BaseDirectory);
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
                    var sizes = (s.Width, s.Height, s.MusicWidth, s.MusicHeight, s.MonitorWidth, s.MonitorHeight);
                    s.MarketWidth = 1000; window.SelectPage(DeckPage.Market, remember: false);
                    double actualMarketWidth = (double)typeof(MainWindow).GetField("_pageWidth", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                    Check("The real market panel ignores a previous oversized saved width and fits its contents", actualMarketWidth >= 240 && actualMarketWidth < 440);
                    window.SelectPage(DeckPage.Monitor, remember: false);
                    Check("Automatic market sizing leaves original page dimensions intact", sizes == (s.Width, s.Height, s.MusicWidth, s.MusicHeight, s.MonitorWidth, s.MonitorHeight));
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
            if (!System.IO.File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, "market-feed.json")))
            {
                app.RefreshMarketAsync().GetAwaiter().GetResult();
                Check("Missing feed configuration becomes an explicit UI error instead of an unhandled loading state", app.MarketError?.Contains("market-feed.json") == true);
                app.RefreshMarketAsync().GetAwaiter().GetResult();
                Check("A failed configuration read releases the refresh gate for recovery", app.MarketError?.Contains("market-feed.json") == true);
            }
            Console.WriteLine($"{_checks} product UI checks passed."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void CheckMarketList(MainWindow window)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var rows = Enumerable.Range(1, 30).Select(n => new MarketCoin("COIN" + n, "COIN" + n + "USDT", true, n, now)).ToArray();
        var snapshot = new MarketFeedSnapshot(now, rows, rows.Take(12).ToArray(), rows.Take(10).ToArray(), null, null);
        CheckTickerWidths(snapshot);
        double preferred = MarketPageView.PreferredWidth(snapshot, MarketRanking.Hot);
        Check("A short market list automatically uses a compact content width", preferred >= 360 && preferred < 440);
        var longRows = new[] { new MarketCoin(new string('W', 35), new string('W', 35) + "USDT", true, 1, now) };
        Check("Automatic market width grows for longer symbols", MarketPageView.PreferredWidth(new MarketFeedSnapshot(now, longRows, [], [], null, null), MarketRanking.Hot) > preferred);
        var view = new MarketPageView(_ => { }, () => { });
        ((Grid)window.FindName("MarketContent")).Children.Add(view);
        view.Update(snapshot, MarketRanking.Hot, null); view.Measure(new Size(600, 230)); view.Arrange(new Rect(0, 0, 600, 230)); view.UpdateLayout();
        Check("Market page renders the entire ranking", view.RowCount == 30 && view.ListScroll.ScrollableHeight > 0);
        foreach (double width in new[] { preferred, 440d, 620d, 1000d })
        {
            view.Measure(new Size(width, 230)); view.Arrange(new Rect(0, 0, width, 230)); view.UpdateLayout();
            var first = (Button)((StackPanel)view.ListScroll.Content).Children[0];
            var content = (Grid)first.Content;
            double rowLeft = first.TransformToAncestor(view).Transform(new Point()).X;
            double rankLeft = content.Children[0].TransformToAncestor(view).Transform(new Point()).X;
            var percent = (TextBlock)content.Children[2];
            Check($"The expanded list includes the signed percentage at {width} DIPs", percent.Text == "+1.00%");
            double percentRight = percent.TransformToAncestor(view).Transform(new Point(percent.ActualWidth, 0)).X;
            Check($"Market rows fill the {width} DIP viewport", first.ActualWidth >= view.ListScroll.ViewportWidth - 4 && rowLeft < 4);
            Check($"Market columns use both edges at {width} DIPs", rankLeft < 20 && percentRight > rowLeft + first.ActualWidth - 20);
        }
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
    private static void CheckTickerWidths(MarketFeedSnapshot snapshot)
    {
        int Visible(double width)
        {
            var ticker = new MarketTickerStrip(width, 24, 1, false);
            ticker.Update(snapshot, MarketRanking.Hot, null);
            ticker.Measure(new Size(width, 24)); ticker.Arrange(new Rect(0, 0, width, 24)); ticker.UpdateLayout();
            var canvas = (Canvas)ticker.Children[0]; var row = (StackPanel)canvas.Children[0];
            var first = (Button)row.Children[0]; var label = (TextBlock)first.Content;
            Check("The collapsed ticker measures the full symbol and signed percentage before scrolling", new System.Windows.Documents.TextRange(label.ContentStart, label.ContentEnd).Text.Contains("+1.00%")
                && label.ActualWidth >= label.DesiredSize.Width && label.ActualWidth > 75);
            int visible = row.Children.Cast<Button>().Count(button => button.TransformToAncestor(canvas).Transform(new Point(button.ActualWidth, 0)).X <= width);
            ticker.Stop(); return visible;
        }
        var narrow = CompactMusicLayout.Calculate(600, 1, true, 3, true, 40, 150, 280);
        var wide = CompactMusicLayout.Calculate(1000, 1, true, 3, true, 40, 150, 280);
        Check("Manual widening shows more complete coins beside original modules", Visible(wide.MarketWidth) > Visible(narrow.MarketWidth));
    }
}
