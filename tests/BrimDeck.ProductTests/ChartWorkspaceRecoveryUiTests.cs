using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Core;

internal static class ChartWorkspaceRecoveryUiTests
{
    internal static void Run(BrimDeck.App app, Action<string, bool> check)
    {
        var saved = app.Settings.Copy();
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var store = app.Store;
        var prices = app.Prices;
        var httpField = typeof(BrimDeck.App).GetField("_chartHttp", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var originalHttp = httpField.GetValue(app);
        string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BrimDeck-Workspace-Recovery-" + Guid.NewGuid().ToString("N"));
        using var fixturePrices = new Pricing(folder);
        using var handler = new CatalogHandler { Failures = 5 };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        SettingsWindow? window = null;
        StackPanel? panel = null;
        try
        {
            typeof(BrimDeck.App).GetProperty(nameof(BrimDeck.App.Store))!.SetValue(app, new SettingsStore(folder));
            typeof(BrimDeck.App).GetProperty(nameof(BrimDeck.App.Prices))!.SetValue(app, fixturePrices);
            BrimDeck.Native.OpenMarketChartConnection.Save(folder, "saved-target", "test-token");
            app.Settings.MarketChartWorkspaces = new() { ["saved-target"] = new() { Name = "Saved target", Interval = "15m", PinnedPanes = [0] } };
            httpField.SetValue(app, http);
            window = new SettingsWindow(app);
            var target = (Panel)typeof(SettingsWindow).GetProperty("Target", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            typeof(SettingsWindow).GetMethod("MarketChartControls", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
            panel = (StackPanel)target.Children[target.Children.Count - 1];
            var selector = panel.Children.OfType<ComboBox>().Single();
            var confirm = panel.Children.OfType<Button>().Single(button => button.Content as string == Loc.T("确认布局并启用", "Confirm layout and enable"));
            check("A saved target is visible before the network loads without approving a cached layout",
                selector.SelectedItem is ChartWorkspace { Id: "saved-target", Name: "Saved target" } && !confirm.IsEnabled && handler.Requests == 0);
            panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            PumpUntil(() => handler.Requests >= 5 && selector.IsEnabled, TimeSpan.FromSeconds(15));
            check("A failed initial load retains the saved target and explains automatic recovery",
                selector.SelectedItem is ChartWorkspace { Id: "saved-target" }
                && panel.Children.OfType<TextBlock>().Any(label => label.Text.Contains(Loc.T("自动重试", "retry automatically"))));
            PumpUntil(() => confirm.IsEnabled, TimeSpan.FromSeconds(20));
            check("The workspace list and layout recover automatically without a refresh click",
                confirm.IsEnabled && selector.Items.Count == 2 && selector.SelectedItem is ChartWorkspace { Id: "saved-target", Name: "Renamed target" });
            check("Automatic recovery preserves pins, interval and unconfirmed layout state",
                app.Settings.MarketChartWorkspaces["saved-target"] is { Interval: "15m", LayoutSignature: "" } rotation
                && rotation.PinnedPanes.SequenceEqual([0]) && handler.Writes == 0);

            // A successful empty catalog is authoritative, unlike a failed request.
            handler.Empty = true;
            var refresh = panel.Children.OfType<Button>().Single(button => button.Content as string == Loc.T("刷新方案及布局", "Refresh workspaces and layout"));
            refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => selector.IsEnabled, TimeSpan.FromSeconds(3));
            check("A deleted target is not restored from the cache after a successful empty catalog", selector.Items.Count == 0 && !confirm.IsEnabled);

            handler.Empty = false; handler.Failures = 5;
            refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => selector.IsEnabled, TimeSpan.FromSeconds(15));
            panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            int requests = handler.Requests;
            PumpUntil(() => false, TimeSpan.FromSeconds(16));
            check("Leaving the workspace settings stops its automatic refresh timer", handler.Requests == requests);

            handler.PairCatalog = true; handler.Failures = 5;
            var resolve = typeof(BrimDeck.App).GetMethod("ResolveCandleChartWorkspaceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var resolved = (Task<string>)resolve.Invoke(app, ["test-token", deadline.Token])!;
            PumpUntil(() => resolved.IsCompleted, TimeSpan.FromSeconds(21));
            check("Countdown preflight recovers after the transport retry batch fails without changing charts",
                resolved.IsCompletedSuccessfully && resolved.Result == "saved-target" && handler.Writes == 0);
            handler.Failures = 100;
            using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            var cancelledRead = (Task<string>)resolve.Invoke(app, ["test-token", cancelled.Token])!;
            PumpUntil(() => cancelledRead.IsCompleted, TimeSpan.FromSeconds(2));
            check("Countdown recovery stops at its caller's cancellation deadline", cancelledRead.IsCanceled && handler.Writes == 0);
            handler.Failures = 0; handler.Unauthorized = true; requests = handler.Requests;
            var unauthorized = (Task<string>)resolve.Invoke(app, ["test-token", CancellationToken.None])!;
            PumpUntil(() => unauthorized.IsCompleted, TimeSpan.FromSeconds(2));
            check("Invalid authorization stops countdown recovery immediately", unauthorized.IsFaulted
                && unauthorized.Exception!.InnerException is InvalidOperationException && handler.Requests == requests + 1);
        }
        finally
        {
            panel?.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            if (window is not null)
            {
                var exiting = typeof(BrimDeck.App).GetProperty(nameof(BrimDeck.App.Exiting))!;
                bool wasExiting = app.Exiting; exiting.SetValue(app, true);
                try { window.Close(); } finally { exiting.SetValue(app, wasExiting); }
            }
            httpField.SetValue(app, originalHttp);
            typeof(BrimDeck.App).GetProperty(nameof(BrimDeck.App.Settings))!.SetValue(app, saved);
            typeof(BrimDeck.App).GetProperty(nameof(BrimDeck.App.Store))!.SetValue(app, store);
            typeof(BrimDeck.App).GetProperty(nameof(BrimDeck.App.Prices))!.SetValue(app, prices);
            SynchronizationContext.SetSynchronizationContext(context);
            if (System.IO.Directory.Exists(folder))
            {
                foreach (string file in System.IO.Directory.GetFiles(folder)) System.IO.File.Delete(file);
                System.IO.Directory.Delete(folder);
            }
        }
    }

    private static void PumpUntil(Func<bool> complete, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        while (!complete() && elapsed.Elapsed < timeout)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
    }

    private sealed class CatalogHandler : HttpMessageHandler
    {
        internal int Failures, Requests, Writes;
        internal bool Empty, PairCatalog, Unauthorized;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (Unauthorized) return new(HttpStatusCode.Unauthorized);
            if (Failures-- > 0) return new(HttpStatusCode.ServiceUnavailable);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = body.RootElement;
            object result;
            if (root.GetProperty("method").GetString() == "initialize") result = new { protocolVersion = "2025-03-26" };
            else
            {
                string name = root.GetProperty("params").GetProperty("name").GetString()!;
                if (name is "chart_interval" or "chart_symbol" or "chart_layout" or "chart_sync") Writes++;
                object data = name switch
                {
                    "chart_list" => Empty ? Array.Empty<object>() : new object[] { new { shortId = "saved-target", name = PairCatalog ? "BTC&ETH" : "Renamed target" }, new { shortId = "other", name = "Other" } },
                    "chart_refresh" => new { workspaceId = "saved-target", workspace = new { multiCharts = new { mode = "1", syncStatus = new { symbol = false, interval = false } } } },
                    _ => new { ok = true }
                };
                result = new { structuredContent = new { data } };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = root.GetProperty("id").GetInt32(), result }), System.Text.Encoding.UTF8, "application/json") };
        }
    }
}
