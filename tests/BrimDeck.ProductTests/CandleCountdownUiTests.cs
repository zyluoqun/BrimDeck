using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck;
using BrimDeck.Core;

internal static class CandleCountdownUiTests
{
    public static void Run(BrimDeck.App app, Action<string, bool> check)
    {
        var boundary = new DateTimeOffset(2026, 10, 28, 8, 0, 0, TimeSpan.Zero);
        var strip = new CandleCountdownStrip(CandlePeriods.All);
        strip.Update(boundary.AddSeconds(-1), true, true);
        var width = strip.ReservedWidth;
        check("All three countdowns display at the same time", strip.Values.SequenceEqual(["00:01", "00:01", "00:00:01"]));
        strip.Update(boundary, true, true);
        strip.Measure(new Size(double.PositiveInfinity, 24));
        check("Rolling into the next candle preserves reserved digit widths", strip.Values.SequenceEqual(["15:00", "60:00", "04:00:00"])
            && Math.Abs(strip.DesiredSize.Width - width) < 1);
        strip.Update(boundary.AddSeconds(-10), false, true);
        check("Unsynchronized time has a visible amber status marker", ((System.Windows.Shapes.Ellipse)strip.Children[0]).Fill is SolidColorBrush { Color: var color }
            && color == (Color)ColorConverter.ConvertFromString("#FF9F43"));
        var value = (TextBlock)((StackPanel)strip.Children[1]).Children[1];
        check("The last ten seconds are highlighted in red", value.Foreground is SolidColorBrush { Color: var red } && red == (Color)ColorConverter.ConvertFromString("#FB7185"));
        strip.Update(boundary.AddSeconds(-10), true, false);
        check("Disabling close highlights restores theme text color", value.Foreground is SolidColorBrush { Color: var normal } && normal == ((SolidColorBrush)UI.Brush(UI.Primary)).Color);

        foreach (var style in new[] { CompactStyle.Notch, CompactStyle.Capsule })
        {
            var s = app.Settings; s.Style = style; s.NotchSummary = s.CapsuleSummary = false;
            s.NotchMusic = s.CapsuleMusic = false; s.NotchClock = s.CapsuleClock = false;
            s.NotchMonitor = s.CapsuleMonitor = true; s.NotchMarket = s.CapsuleMarket = true;
            s.NotchCandleCountdown = s.CapsuleCandleCountdown = true; s.CandleCountdownPeriods = CandlePeriods.All;
            s.NotchAutoWidth = s.CapsuleAutoWidth = false;
            s.Maximized = s.Borderless = s.Exclusive = WindowBehavior.Normal; s.FullscreenRules = [];
            var window = new MainWindow(app);
            typeof(BrimDeck.App).GetProperty(nameof(BrimDeck.App.Deck))!.SetValue(app, window);
            try
            {
                var originalDpi = VisualTreeHelper.GetDpi(window);
                foreach (double monitorWidth in new[] { 0d, 1024d, 1920d })
                foreach (double dpi in new[] { 1d, 1.25, 1.5, 2d })
                foreach (double surface in new[] { 320d, 640d, 1000d, 1024d, 1100d })
                {
                    s.NotchWidth = s.CapsuleWidth = surface; window.ApplySettings();
                    if (monitorWidth > 0)
                    {
                        var monitor = new DipRect(0, 0, monitorWidth, 768);
                        var shape = CollapsedGeometry.Resolve(s, style, monitor, monitor, dpi);
                        typeof(MainWindow).GetProperty(nameof(MainWindow.Collapsed), BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, shape);
                    }
                    VisualTreeHelper.SetRootDpi(window, new DpiScale(dpi, dpi)); window.RenderCompact();
                    var compact = (Grid)window.FindName("Compact");
                    compact.Measure(new Size(compact.Width, compact.Height)); compact.Arrange(new Rect(0, 0, compact.Width, compact.Height)); compact.UpdateLayout();
                    var countdown = (CandleCountdownStrip)typeof(MainWindow).GetField("_compactCountdown", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                    var ticker = (MarketTickerStrip)typeof(MainWindow).GetField("_marketStrip", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                    var end = countdown.TransformToAncestor(compact).Transform(new Point(countdown.ActualWidth, 0)).X;
                    var start = countdown.TransformToAncestor(compact).Transform(new Point()).X;
                    var tickerEnd = ticker.TransformToAncestor(compact).Transform(new Point(ticker.ActualWidth, 0)).X;
                    check($"{style}: three countdowns fit without overlapping ticker at {surface} DIPs / {dpi} DPI / {monitorWidth} monitor (ticker end {tickerEnd:0.###}, countdown {start:0.###}..{end:0.###}, row {compact.Width:0.###})", countdown.Values.Count == 3
                        && start >= tickerEnd - 1 && end <= compact.Width + 1 && end > start);
                    check($"{style}: monitoring still has room beside countdowns at {surface} DIPs", window.CurrentCompactLayout!.MonitorWidth > 0);
                    check($"{style}: upstream whole-reading layout keeps an actual monitor beside the custom modules at {surface} DIPs",
                        typeof(MainWindow).GetField("_compactStrip", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window) is MonitorStrip { ActualWidth: > 0 });
                }
                VisualTreeHelper.SetRootDpi(window, originalDpi);
                s.NotchClock = s.CapsuleClock = true; window.RenderCompact();
                check($"{style}: clock and countdown coexist", typeof(MainWindow).GetField("_compactClock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window) is ClockFace);
                var timeRow = (Grid)window.FindName("Compact");
                timeRow.Measure(new Size(timeRow.Width, timeRow.Height)); timeRow.Arrange(new Rect(0, 0, timeRow.Width, timeRow.Height)); timeRow.UpdateLayout();
                var clock = (ClockFace)typeof(MainWindow).GetField("_compactClock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                var afterClock = (CandleCountdownStrip)typeof(MainWindow).GetField("_compactCountdown", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                double clockRight = clock.TransformToAncestor(timeRow).Transform(new Point(clock.ActualWidth, 0)).X;
                double countdownLeft = afterClock.TransformToAncestor(timeRow).Transform(new Point()).X;
                check($"{style}: countdowns stay after the original clock", countdownLeft >= clockRight + 1);
                SavePreview(timeRow, style + "-clock-countdowns.png");
                s.NotchCandleCountdown = s.CapsuleCandleCountdown = false; window.RenderCompact();
                check($"{style}: disabling countdown preserves the original clock", typeof(MainWindow).GetField("_compactCountdown", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window) is null
                    && typeof(MainWindow).GetField("_compactClock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window) is ClockFace);
                s.NotchCandleCountdown = s.CapsuleCandleCountdown = true;
                var page = new MarketPageView(_ => { }, () => { });
                page.UpdateCountdown(s, boundary, true); page.Update(MarketFeedSnapshot.Empty, MarketRanking.Hot, null);
                var expanded = page.Children.OfType<Border>().Single();
                check($"{style}: expanded market shows exactly the same three periods", expanded.Child is CandleCountdownStrip { Values.Count: 3 });
                s.CandleCountdownPeriods = CandlePeriods.OneHour; page.UpdateCountdown(s, boundary, true); window.RenderCompact();
                check($"{style}: changing periods updates compact and expanded views", expanded.Child is CandleCountdownStrip { Values.Count: 1 }
                    && ((CandleCountdownStrip)typeof(MainWindow).GetField("_compactCountdown", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!).Values.Count == 1);
                if (style == CompactStyle.Capsule) CheckSettings(app, check);
                CheckCombinedRow(app, window, check);
            }
            finally { typeof(MainWindow).GetField("_retiring", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true); window.Close(); }
        }
    }

    private static void CheckCombinedRow(BrimDeck.App app, MainWindow window, Action<string, bool> check)
    {
        var s = app.Settings;
        var before = (s.PanelScale, s.NotchMusic, s.CapsuleMusic, s.NotchSummary, s.CapsuleSummary, s.MusicText, s.CandleCountdownPeriods);
        var track = typeof(MainWindow).GetField("_mediaTrack", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var previous = track.GetValue(window);
        try
        {
            s.NotchMusic = s.CapsuleMusic = s.NotchSummary = s.CapsuleSummary = true;
            s.MusicText = CompactMusicText.Title; s.CandleCountdownPeriods = CandlePeriods.All;
            foreach (int scale in PanelScales.Choices)
            {
                s.PanelScale = scale; window.ApplySettings();
                track.SetValue(window, new MediaTrack { Id = "merge-layout-test", Title = "Combined layout", State = BrimDeck.Core.MediaState.Paused });
                window.RenderCompact();
                var compact = (Grid)window.FindName("Compact");
                compact.Measure(new Size(compact.Width, compact.Height)); compact.Arrange(new Rect(0, 0, compact.Width, compact.Height)); compact.UpdateLayout();
                foreach (string name in new[] { "_quotaCarousel", "_compactMarquee", "_compactEqualizer", "_compactClock", "_compactStrip", "_marketStrip", "_compactCountdown" })
                {
                    var control = typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window) as FrameworkElement;
                    check($"{s.Style}: {name} coexists with every module at {scale}%", control is { ActualWidth: > 0 }
                        && control.TransformToAncestor(compact).Transform(new Point()).X >= -1
                        && control.TransformToAncestor(compact).Transform(new Point(control.ActualWidth, 0)).X <= compact.Width + 1);
                }
            }
        }
        finally
        {
            (s.PanelScale, s.NotchMusic, s.CapsuleMusic, s.NotchSummary, s.CapsuleSummary, s.MusicText, s.CandleCountdownPeriods) = before;
            track.SetValue(window, previous);
        }
    }

    private static void CheckSettings(BrimDeck.App app, Action<string, bool> check)
    {
        string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BrimDeck-Countdown-Settings-" + Guid.NewGuid().ToString("N"));
        using var prices = new Pricing(folder);
        typeof(BrimDeck.App).GetProperty(nameof(BrimDeck.App.Prices))!.SetValue(app, prices);
        typeof(BrimDeck.App).GetProperty(nameof(BrimDeck.App.Store))!.SetValue(app, new SettingsStore(folder));
        var settings = new SettingsWindow(app);
        try
        {
            typeof(SettingsWindow).GetField("_candleCountdownOpen", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(settings, true);
            settings.ShowPage(1);
            var content = (StackPanel)typeof(SettingsWindow).GetField("_content", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(settings)!;
            var names = Descendants(content).OfType<CheckBox>().Select(System.Windows.Automation.AutomationProperties.GetName).ToList();
            string clock = Loc.T("时间", "Clock"), countdown = Loc.T("收盘倒计时", "Candle countdown");
            var toggles = Descendants(content).OfType<CheckBox>().ToArray();
            var clockToggle = toggles.Single(box => System.Windows.Automation.AutomationProperties.GetName(box) == clock);
            var countdownToggle = toggles.Single(box => System.Windows.Automation.AutomationProperties.GetName(box) == countdown);
            var clockRow = (StackPanel)((Grid)clockToggle.Parent).Parent;
            var countdownRow = (StackPanel)((Grid)countdownToggle.Parent).Parent;
            var group = (StackPanel)clockRow.Parent;
            check("Collapsed settings places countdown immediately after Clock", ReferenceEquals(group, countdownRow.Parent)
                && group.Children.IndexOf(countdownRow) == group.Children.IndexOf(clockRow) + 2);
            check("Countdown settings offers only the approved three periods", names.Count(name => name.StartsWith(Loc.T("收盘倒计时 ", "Candle countdown "))) == 3);
            content.Measure(new Size(650, double.PositiveInfinity)); content.Arrange(new Rect(0, 0, 650, content.DesiredSize.Height)); content.UpdateLayout();
            SavePreview(content, "countdown-settings.png");
        }
        finally { settings.Close(); }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Descendants(child)) yield return descendant;
    }
    private static void SavePreview(FrameworkElement visual, string name)
    {
        if (Environment.GetEnvironmentVariable("BRIMDECK_COUNTDOWN_PREVIEWS") is not { Length: > 0 } folder) return;
        System.IO.Directory.CreateDirectory(folder);
        int width = (int)Math.Ceiling(visual.ActualWidth), height = (int)Math.Ceiling(visual.ActualHeight);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(UI.Brush("#18181B"), null, new Rect(0, 0, width, height));
            context.DrawRectangle(new VisualBrush(visual), null, new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap(width * 2, height * 2, 192, 192, PixelFormats.Pbgra32); bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = System.IO.File.Create(System.IO.Path.Combine(folder, name)); encoder.Save(file);
    }
}
