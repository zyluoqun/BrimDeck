using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Core;

internal static class CapsuleAvoidanceUiTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    internal static void Run(BrimDeck.App app, Action<string, bool> check)
    {
        string language = Loc.Language;
        try
        {
            foreach (string chosen in new[] { Loc.Chinese, Loc.English })
            {
                Loc.Use(chosen);
                RunInLanguage(app, check);
            }
        }
        finally { Loc.Use(language); }
    }

    private static void RunInLanguage(BrimDeck.App app, Action<string, bool> check)
    {
        var saved = app.Settings;
        var savedDeck = app.Deck;
        var savedStore = app.Store;
        var savedPrices = app.Prices;
        bool savedHidden = app.UserHidden;
        string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BrimDeck-Avoidance-" + Guid.NewGuid().ToString("N"));
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
                Language = Loc.Language, Style = CompactStyle.Capsule, Maximized = WindowBehavior.Hide,
                Borderless = WindowBehavior.Line, Exclusive = WindowBehavior.Hide,
                FullscreenRules = [new FullscreenRule("test-app") { Maximized = WindowBehavior.Line, Borderless = WindowBehavior.Hide, Exclusive = WindowBehavior.Normal }],
                Apps = [], UsagePage = false, UsageAutoSync = false, MusicPage = false, MonitorPage = false, MarketPage = false,
                CapsuleSummary = false, CapsuleMusic = false, CapsuleMonitor = false, CapsuleClock = false,
                CapsuleMarket = false, NotchMarket = false, LineMarket = false, CandleCountdownPeriods = CandlePeriods.None,
                ShowTrayIcon = false, TrimMemoryWhenIdle = false, BackgroundWindows = false, Animations = false
            };
            Set(app, nameof(BrimDeck.App.Settings), settings);
            Set(app, nameof(BrimDeck.App.Store), new SettingsStore(folder));
            Set(app, nameof(BrimDeck.App.Prices), prices);
            // Updating settings must stay inside this fixture and never show a real overlay on the desktop.
            Set(app, nameof(BrimDeck.App.UserHidden), true);
            deck = new MainWindow(app);
            Set(app, nameof(BrimDeck.App.Deck), deck);
            window = new SettingsWindow(app);
            typeof(SettingsWindow).GetField("_scroll", Private)!.SetValue(window, new ScrollViewer());
            var content = (StackPanel)typeof(SettingsWindow).GetField("_content", Private)!.GetValue(window)!;
            void Build() { content.Children.Clear(); typeof(SettingsWindow).GetMethod("WindowRules", Private)!.Invoke(window, null); }
            string global = Loc.T("有窗口最大化时", "When a window is maximized");
            string own = "test-app · " + Loc.T("窗口最大化时", "Maximized");
            RadioButton[] Picks(string prefix) => Descendants(content).OfType<RadioButton>()
                .Where(pick => AutomationProperties.GetName(pick).StartsWith(prefix + " · ", StringComparison.Ordinal)).ToArray();
            bool Picked(string prefix, WindowBehavior behavior, bool enabled)
            {
                var picks = Picks(prefix);
                return picks.Length >= 3 && picks.All(pick => pick.IsEnabled == enabled)
                    && picks.Single(pick => pick.IsChecked == true).Content as string == SettingsWindow.BehaviorName(behavior);
            }
            Build();
            check("Avoidance settings initially show both saved maximized choices", Picked(global, WindowBehavior.Hide, true) && Picked(own, WindowBehavior.Line, true));
            var toggle = Descendants(content).OfType<CheckBox>().Single(box => AutomationProperties.GetName(box) == Loc.T("最大化窗口避让胶囊", "Keep maximized windows clear of the capsule"));
            toggle.IsChecked = true;
            check("Enabling avoidance immediately selects and locks Keep style globally and per app", Picked(global, WindowBehavior.Normal, false) && Picked(own, WindowBehavior.Normal, false));
            check("The avoidance switch changes the effective rule without overwriting saved choices", app.Settings.Behavior(ScreenContext.Maximized, "test-app") == WindowBehavior.Normal
                && app.Settings.Maximized == WindowBehavior.Hide && app.Settings.FullscreenRules[0].Maximized == WindowBehavior.Line);
            check("Both global full-screen controls remain editable and unchanged",
                Picked(Loc.T("有应用处于无边框全屏时", "When an app is in borderless full screen"), WindowBehavior.Line, true)
                && Picked(Loc.T("有应用处于独占全屏时", "When an app is in exclusive full screen"), WindowBehavior.Hide, true));
            check("Both app-specific full-screen controls remain editable and unchanged",
                Picked("test-app · " + Loc.T("无边框全屏时", "Borderless full screen"), WindowBehavior.Hide, true)
                && Picked("test-app · " + Loc.T("独占全屏时", "Exclusive full screen"), WindowBehavior.Normal, true));
            toggle.IsChecked = false;
            check("Disabling avoidance immediately restores and unlocks both original choices", Picked(global, WindowBehavior.Hide, true) && Picked(own, WindowBehavior.Line, true));
            app.Settings.ReserveCapsuleSpace = true;
            foreach (var style in new[] { CompactStyle.Notch, CompactStyle.Line })
            {
                app.Settings.Style = style; Build();
                check($"A remembered avoidance switch does not lock rules for {style}", Picked(global, WindowBehavior.Hide, true) && Picked(own, WindowBehavior.Line, true));
            }
            app.Settings.Style = CompactStyle.Capsule; Build();
            check("Reopening capsule settings with avoidance enabled displays the effective locked choices", Picked(global, WindowBehavior.Normal, false) && Picked(own, WindowBehavior.Normal, false));
        }
        finally
        {
            Dispatcher.CurrentDispatcher.Hooks.OperationPosted -= posted;
            foreach (var operation in pending) operation.Abort();
            ((DispatcherTimer)typeof(BrimDeck.App).GetField("_save", Private)!.GetValue(app)!).Stop();
            bool exiting = app.Exiting;
            Set(app, nameof(BrimDeck.App.Exiting), true);
            try { window?.Close(); deck?.Close(); }
            finally { Set(app, nameof(BrimDeck.App.Exiting), exiting); }
            Set(app, nameof(BrimDeck.App.Settings), saved); Set(app, nameof(BrimDeck.App.Deck), savedDeck);
            Set(app, nameof(BrimDeck.App.Store), savedStore); Set(app, nameof(BrimDeck.App.Prices), savedPrices);
            Set(app, nameof(BrimDeck.App.UserHidden), savedHidden);
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
