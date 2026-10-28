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
            using var lyrics = new LyricsService();
            typeof(BrimDeck.App).GetProperty("Lyrics", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(app, lyrics);
            CheckChartCredential();
            ChromeBridgeTests.Run(Check).GetAwaiter().GetResult();
            ChromeFocusTests.Run(Check).GetAwaiter().GetResult();
            app.Updates = new AppUpdates(BrimDeck.Updates.GitHubUpdates.Create(AppContext.BaseDirectory), AppContext.BaseDirectory);
            foreach (var style in new[] { CompactStyle.Notch, CompactStyle.Capsule })
            {
                var s = app.Settings; s.Style = style; s.NotchSummary = s.CapsuleSummary = false; s.NotchMusic = s.CapsuleMusic = false;
                s.NotchClock = s.CapsuleClock = false; s.NotchMonitor = s.CapsuleMonitor = true;
                s.NotchCandleCountdown = s.CapsuleCandleCountdown = false;
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
                    window.TrackCompactHover(true, true);
                    Check($"{style}: hovering the market leaves the panel collapsed", !hover.IsEnabled && !window.IsExpanded);
                    window.TrackCompactHover(true, false);
                    Check($"{style}: moving from market to another module starts normal hover", hover.IsEnabled);
                    window.TrackCompactHover(true, true);
                    Check($"{style}: entering market cancels a pending expansion", !hover.IsEnabled);
                    window.TrackCompactHover(false, false);
                    var marketArea = window.CompactMarketHoverArea;
                    Check($"{style}: market hover geometry exists before controls are measured", !marketArea.IsEmpty && marketArea.Width > 0);
                    Check($"{style}: the outer padding beside the last market slot cannot open the panel", window.IsMarketHoverPoint(new Point(window.Collapsed.Surface.Right - 1, window.Collapsed.Surface.Bottom - 1)));
                    var currentTicker = (MarketTickerStrip)typeof(MainWindow).GetField("_marketStrip", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                    var compact = (Grid)window.FindName("Compact"); compact.Measure(new Size(compact.Width, compact.Height));
                    compact.Arrange(new Rect(0, 0, compact.Width, compact.Height)); compact.UpdateLayout();
                    double tickerLeft = window.Collapsed.Surface.X + (window.Collapsed.Surface.Width - compact.Width) / 2
                        + currentTicker.TransformToAncestor(compact).Transform(new Point()).X;
                    Check($"{style}: the stable hover area covers the real arranged ticker", marketArea.Left <= tickerLeft + 1 && marketArea.Right >= tickerLeft + currentTicker.ActualWidth - 1);
                    window.TrackCompactHover(true, false);
                    currentTicker.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = UIElement.MouseEnterEvent });
                    Check($"{style}: entering the ticker cancels hover before the next pointer poll", !hover.IsEnabled);
                    int oldDelay = s.OpenDelay; s.OpenDelay = 0; window.PointerChanged(true);
                    Check($"{style}: passing another module on the way to a coin waits for pointer intent", hover.Interval >= TimeSpan.FromMilliseconds(80));
                    window.PointerChanged(false); s.OpenDelay = oldDelay;
                    s.NotchMonitor = s.CapsuleMonitor = false; window.RenderCompact();
                    Check($"{style}: a market-only surface also blocks hover in both rounded ends", window.CompactMarketHoverArea.Left == window.Collapsed.Surface.X
                        && window.CompactMarketHoverArea.Right == window.Collapsed.Surface.Right);
                    s.NotchMonitor = s.CapsuleMonitor = true; window.RenderCompact();
                    Check("The real panel contains all four module buttons", new[] { "AIUsageButton", "MusicPageButton", "MonitorPageButton", "MarketPageButton" }.All(name => window.FindName(name) is Button));
                    s.PageOrder = [DeckPage.Market, DeckPage.Monitor, DeckPage.Music, DeckPage.Usage];
                    window.SelectPage(DeckPage.Market, remember: false);
                    var pageButtons = (Panel)((Button)window.FindName("AIUsageButton")).Parent;
                    Check("The market page follows the upstream customizable page order", pageButtons.Children.OfType<Button>().Select(button => button.Name)
                        .SequenceEqual(["MarketPageButton", "MonitorPageButton", "MusicPageButton", "AIUsageButton"]));
                    s.PageOrder = [.. DeckPages.Order]; window.SelectPage(DeckPage.Usage, remember: false);
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
            CandleCountdownUiTests.Run(app, Check);
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
        MarketCoin? selected = null;
        var clickable = new MarketPageView(_ => { }, () => { }, coin => { selected = coin; return Task.CompletedTask; });
        clickable.Update(snapshot, MarketRanking.Hot, null);
        ((Button)((StackPanel)clickable.ListScroll.Content).Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check("Expanded coin clicks call the shared chart controller with the exact market", selected?.Symbol == "COIN1USDT" && selected.Perpetual);
        clickable.SetPending([rows[0]]);
        var firstRow = (Grid)((Button)((StackPanel)clickable.ListScroll.Content).Children[0]).Content;
        var symbolLabel = (TextBlock)firstRow.Children[1];
        var ring = symbolLabel.Inlines.OfType<System.Windows.Documents.InlineUIContainer>().Select(inline => inline.Child).OfType<MarketLoadingRing>().Single();
        Check("The clicked market row shows its inline loading ring", ring.IsLoading && ring.Visibility == Visibility.Visible);
        Check("Expanded market rows also put the loading ring before the coin name", symbolLabel.Inlines.FirstInline is System.Windows.Documents.InlineUIContainer);
        clickable.Update(snapshot with { Hot = rows.Reverse().ToArray() }, MarketRanking.Hot, null);
        Check("A ranking refresh cannot replace the selected row while its chart is pending", ReferenceEquals(firstRow, ((Button)((StackPanel)clickable.ListScroll.Content).Children[0]).Content) && ring.IsLoading);
        clickable.SetPending([]);
        Check("Completing a chart request hides the row spinner", !ring.IsLoading && ring.Visibility == Visibility.Hidden);
    }
    private static void CheckTickerWidths(MarketFeedSnapshot snapshot)
    {
        int Visible(double width)
        {
            MarketCoin? clicked = null;
            var ticker = new MarketTickerStrip(width, 24, 1, false, switchChart: coin => { clicked = coin; return Task.CompletedTask; });
            ticker.Update(snapshot, MarketRanking.Hot, null);
            ticker.Measure(new Size(width, 24)); ticker.Arrange(new Rect(0, 0, width, 24)); ticker.UpdateLayout();
            var canvas = (Canvas)ticker.Children[0]; var row = (StackPanel)canvas.Children[0];
            var first = (Button)row.Children[0]; var label = (TextBlock)first.Content;
            Check("Market strip and coin buttons have no hover tooltip", !ToolTipService.GetIsEnabled(ticker) && !ToolTipService.GetIsEnabled(first));
            first.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("Ticker clicks call the shared chart controller without opening a URL", clicked?.Symbol == "COIN1USDT" && clicked.Perpetual);
            Check("The collapsed ticker measures the full symbol and signed percentage before scrolling", new System.Windows.Documents.TextRange(label.ContentStart, label.ContentEnd).Text.Contains("+1.00%")
                && label.ActualWidth >= label.DesiredSize.Width && label.ActualWidth > 75);
            double beforeWidth = first.ActualWidth;
            var ring = label.Inlines.OfType<System.Windows.Documents.InlineUIContainer>().Select(inline => inline.Child).OfType<MarketLoadingRing>().Single();
            Check("The loading ring precedes an adjacent rank and coin name", label.Inlines.FirstInline is System.Windows.Documents.InlineUIContainer
                && label.Inlines.ElementAt(1) is System.Windows.Documents.Run rank && rank.Text == "1."
                && rank.NextInline is System.Windows.Documents.Run name && name.Text == snapshot.Hot[0].Asset + " ");
            ticker.SetPending([snapshot.Hot[0]]); ticker.UpdateLayout();
            Check("Ticker loading reserves its width without shifting the selected coin", ring.IsLoading && Math.Abs(first.ActualWidth - beforeWidth) < .01);
            ticker.Update(snapshot with { Hot = snapshot.Hot.Reverse().ToArray() }, MarketRanking.Hot, null);
            Check("A ticker ranking refresh keeps the pending coin and spinner in place", ReferenceEquals(first, row.Children[0]) && ring.IsLoading);
            var pendingWheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = UIElement.MouseWheelEvent };
            canvas.RaiseEvent(pendingWheel);
            Check("Pending ticker requests also block reduced-motion wheel scrolling", pendingWheel.Handled);
            ticker.SetPending([]);
            Check("Finishing a ticker request clears the inline loading indicator", !ring.IsLoading);
            ticker.Measure(new Size(width, 24)); ticker.Arrange(new Rect(0, 0, width, 24)); ticker.UpdateLayout();
            int visible = row.Children.Cast<Button>().Count(button => button.TransformToAncestor(canvas).Transform(new Point(button.ActualWidth, 0)).X <= width);
            ticker.Stop(); return visible;
        }
        var narrow = CompactMusicLayout.Calculate(600, 1, true, 3, true, 40, 150, 280);
        var wide = CompactMusicLayout.Calculate(1000, 1, true, 3, true, 40, 150, 280);
        Check("Manual widening shows more complete coins beside original modules", Visible(wide.MarketWidth) > Visible(narrow.MarketWidth));
        var cycling = new MarketTickerStrip(180, 24, 1, true);
        cycling.Update(snapshot, MarketRanking.Hot, null);
        var cycleRow = (StackPanel)((Canvas)cycling.Children[0]).Children[0];
        var ranks = cycleRow.Children.Cast<Button>().Select(button => ((TextBlock)button.Content).Inlines.OfType<System.Windows.Documents.Run>().First().Text).ToArray();
        var expected = Enumerable.Range(1, snapshot.Hot.Count).Select(index => index + ".").ToArray();
        Check("Animated ticker copies preserve ranking numbers across the scroll boundary", ranks.SequenceEqual(expected.Concat(expected)));
        cycling.Stop();
    }
    private static void CheckChartCredential()
    {
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BrimDeck-Chart-" + Guid.NewGuid().ToString("N"));
        try
        {
            System.IO.Directory.CreateDirectory(folder);
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "portable.flag"), "BrimDeck portable market edition");
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, PortableUpdate.ListFile), "BrimDeck.exe");
            Check("The custom portable edition cannot install an upstream update over its custom features",
                BrimDeck.Updates.GitHubUpdates.Mode("BrimDeck-Custom-Test", folder) == UpdateMode.None);
            BrimDeck.Native.OpenMarketChartConnection.Save(folder, "https://openmarket.xyz/chart/test-layout", "private-test-token");
            var restored = BrimDeck.Native.OpenMarketChartConnection.Load(folder);
            Check("Chart credentials round-trip through Windows user encryption", restored.WorkspaceId == "test-layout" && restored.Token == "private-test-token");
            Check("Chart configuration never persists a plaintext token", System.IO.Directory.GetFiles(folder).All(path => !System.Text.Encoding.UTF8.GetString(System.IO.File.ReadAllBytes(path)).Contains("private-test-token")));
            BrimDeck.Native.OpenMarketChartConnection.Save(folder, "other-layout", "");
            Check("Saving a workspace with a blank token retains the encrypted credential", BrimDeck.Native.OpenMarketChartConnection.Load(folder).Token == "private-test-token");
        }
        finally
        {
            if (System.IO.Directory.Exists(folder))
            {
                foreach (var file in System.IO.Directory.GetFiles(folder)) System.IO.File.Delete(file);
                System.IO.Directory.Delete(folder);
            }
        }
    }
}
