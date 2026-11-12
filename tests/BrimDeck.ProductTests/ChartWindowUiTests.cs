using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Core;

internal static class ChartWindowUiTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    internal static void Run(BrimDeck.App app, Action<string, bool> check)
    {
        var saved = app.Settings;
        var savedDeck = app.Deck;
        var savedStore = app.Store;
        var savedPrices = app.Prices;
        bool savedHidden = app.UserHidden;
        string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BrimDeck-Chart-Window-" + Guid.NewGuid().ToString("N"));
        using var prices = new Pricing(folder);
        SettingsWindow? window = null;
        MainWindow? deck = null;
        var pending = new List<DispatcherOperation>();
        DispatcherHookEventHandler posted = (_, e) => pending.Add(e.Operation);
        Dispatcher.CurrentDispatcher.Hooks.OperationPosted += posted;
        try
        {
            var settings = new DeckSettings
            {
                Language = Loc.Language,
                Apps = [], UsagePage = false, UsageAutoSync = false, MusicPage = false, MonitorPage = false, MarketPage = false,
                CapsuleSummary = false, CapsuleMusic = false, CapsuleMonitor = false, CapsuleClock = false,
                CapsuleMarket = false, NotchMarket = false, LineMarket = false, CandleCountdownPeriods = CandlePeriods.None,
                ShowTrayIcon = false, TrimMemoryWhenIdle = false, BackgroundWindows = false, Animations = false,
                MarketChartDisplay = new("missing-monitor", "Disconnected chart monitor"), MarketChartFullscreen = false,
                MarketChartWorkspaces = new() { ["one"] = new() { Name = "First", Interval = "1h" }, ["two"] = new() { Name = "Second", Interval = "4h" } }
            };
            Set(app, nameof(BrimDeck.App.Settings), settings); Set(app, nameof(BrimDeck.App.Store), new SettingsStore(folder));
            Set(app, nameof(BrimDeck.App.Prices), prices); Set(app, nameof(BrimDeck.App.UserHidden), true);
            deck = new MainWindow(app); Set(app, nameof(BrimDeck.App.Deck), deck);
            window = new SettingsWindow(app);
            FrameworkElement Build() => (FrameworkElement)typeof(SettingsWindow).GetMethod("MarketChartWindowControls", Private)!.Invoke(window, null)!;
            ComboBox Choice(DependencyObject root) => Descendants(root).OfType<ComboBox>().Single();
            CheckBox Fullscreen(DependencyObject root) => Descendants(root).OfType<CheckBox>().Single();
            var controls = Build();
            check("Disconnected chart selection stays visible and fullscreen remains off when settings reopen",
                Choice(controls).SelectedItem as string == "Disconnected chart monitor" + Loc.T("（未连接）", " (not connected)") && Fullscreen(controls).IsChecked == false);
            controls.Measure(new Size(600, double.PositiveInfinity));
            controls.Arrange(new Rect(0, 0, 600, controls.DesiredSize.Height)); controls.UpdateLayout();
            check("The shared chart selector fits the settings row", Choice(controls).ActualWidth == 320
                && Choice(controls).TranslatePoint(new Point(320, 0), controls).X <= 600);
            Fullscreen(controls).IsChecked = true;
            check("The real F11 toggle updates the shared setting", app.Settings.MarketChartFullscreen);
            Fullscreen(controls).IsChecked = false;
            var displays = BrimDeck.Native.Displays.Current();
            if (displays.Count > 0)
            {
                Choice(controls).SelectedIndex = 0;
                check("The real chart selector saves a stable monitor identity", app.Settings.MarketChartDisplay?.Id == displays[0].Id);
            }
            app.Store.Save(app.Settings);
            var loaded = app.Store.Load();
            check("Shared chart options save to disk without altering individual workspaces", !loaded.MarketChartFullscreen
                && loaded.MarketChartDisplay == app.Settings.MarketChartDisplay && loaded.MarketChartWorkspaces["one"].Interval == "1h"
                && loaded.MarketChartWorkspaces["two"].Interval == "4h");
            var reopened = Build();
            check("Reopening the chart controls restores the chosen monitor and disabled fullscreen",
                Choice(reopened).SelectedItem as string == Choice(controls).SelectedItem as string && Fullscreen(reopened).IsChecked == false);
        }
        finally
        {
            Dispatcher.CurrentDispatcher.Hooks.OperationPosted -= posted;
            foreach (var operation in pending) operation.Abort();
            ((DispatcherTimer)typeof(BrimDeck.App).GetField("_save", Private)!.GetValue(app)!).Stop();
            bool exiting = app.Exiting; Set(app, nameof(BrimDeck.App.Exiting), true);
            try { window?.Close(); deck?.Close(); } finally { Set(app, nameof(BrimDeck.App.Exiting), exiting); }
            Set(app, nameof(BrimDeck.App.Settings), saved); Set(app, nameof(BrimDeck.App.Deck), savedDeck);
            Set(app, nameof(BrimDeck.App.Store), savedStore); Set(app, nameof(BrimDeck.App.Prices), savedPrices); Set(app, nameof(BrimDeck.App.UserHidden), savedHidden);
            PanelTheme.Use(saved);
            if (System.IO.Directory.Exists(folder))
            {
                foreach (string file in System.IO.Directory.GetFiles(folder)) System.IO.File.Delete(file);
                System.IO.Directory.Delete(folder);
            }
        }
    }

    private static void Set(BrimDeck.App app, string property, object? value) => typeof(BrimDeck.App).GetProperty(property)!.SetValue(app, value);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Descendants(child)) yield return descendant;
    }
}
