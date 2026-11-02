using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    private Grid MarketChartLayoutPreview(ChartLayout layout, List<int> pinned, Action changed)
    {
        var grid = new Grid { Height = Math.Clamp(layout.Rows * 86.0, 120, 320), Margin = new Thickness(0, 8, 0, 8) };
        for (int col = 0; col < layout.Columns; col++) grid.ColumnDefinitions.Add(new());
        for (int row = 0; row < layout.Rows; row++) grid.RowDefinitions.Add(new());
        foreach (var pane in layout.Panes)
        {
            var button = new Button { Style = (Style)FindResource("SettingsOutlineButton"), Margin = new Thickness(3),
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, MinWidth = 0, Padding = new Thickness(4) };
            var label = new TextBlock { TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            button.Content = label;
            void Update()
            {
                bool keep = pinned.Contains(pane.Index);
                label.Text = Loc.T($"图 {pane.Index + 1}", $"Chart {pane.Index + 1}")
                    + (pane.Symbol.Length > 0 ? " · " + pane.Symbol : "") + (pane.Interval.Length > 0 ? " · " + pane.Interval : "")
                    + "\n" + (keep ? Loc.T("已固定", "Pinned") : Loc.T("参与轮换", "Rotates"));
                button.BorderBrush = UI.Brush(keep ? _palette.Accent : _palette.Border);
                button.BorderThickness = new Thickness(keep ? 2 : 1);
                AutomationProperties.SetName(button, label.Text.Replace('\n', ' '));
            }
            Update();
            button.Click += (_, _) => { if (!pinned.Remove(pane.Index)) pinned.Add(pane.Index); Update(); changed(); };
            Grid.SetColumn(button, pane.Column); Grid.SetRow(button, pane.Row);
            Grid.SetColumnSpan(button, pane.ColumnSpan); Grid.SetRowSpan(button, pane.RowSpan); grid.Children.Add(button);
        }
        return grid;
    }

    private void MarketChartControls()
    {
        var heading = TextLine(Loc.T("网页图表联动", "Browser chart connection"), 13, TextPrimary, FontWeights.SemiBold);
        heading.Margin = new Thickness(0, 16, 0, 8); Target.Children.Add(heading);
        var panel = new StackPanel(); Target.Children.Add(panel);
        panel.Children.Add(TextLine(Loc.T("先选择胶囊切换使用的方案，再点击布局中的图表设置固定。未固定的图表按编号轮换，使用此方案所选的周期；固定图表保留币种和周期。目标方案仍在 2 号显示器全屏打开。", "Choose the workspace for market clicks, then click charts below to pin them. Unpinned charts rotate using this workspace's interval; pinned charts keep their market and interval. The target workspace still opens fullscreen on display 2."), 11, TextSecondary));
        var status = TextLine("", 11, TextSecondary); status.Margin = new Thickness(0, 6, 0, 6);
        string legacyWorkspace = "", savedWorkspace = "";
        try
        {
            savedWorkspace = _app.ChartConnection.WorkspaceId;
            if (S.MarketChartWorkspaces.Count == 0) legacyWorkspace = savedWorkspace;
        }
        catch (Exception ex) when (ex is System.IO.IOException or ArgumentException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException)
        { status.Text = ex.Message; }
        var summary = TextLine("", 12, TextPrimary);
        summary.VerticalAlignment = VerticalAlignment.Center;
        var rotationSummary = TextLine("", 12, TextPrimary);
        var intervals = ChartIntervals.All.ToArray();
        var intervalChoice = new ComboBox { Style = (Style)FindResource("SettingsCombo"), Width = 128,
            ItemsSource = intervals.Select(ChartIntervals.Label).ToArray(), SelectedIndex = Array.IndexOf(intervals, ChartIntervals.Default), IsEnabled = false };
        AutomationProperties.SetName(intervalChoice, Loc.T("方案切换周期", "Workspace switch interval"));
        var summaryRow = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        summaryRow.ColumnDefinitions.Add(new()); summaryRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        summaryRow.Children.Add(summary);
        var intervalControls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 0, 0, 0) };
        var intervalLabel = TextLine(Loc.T("切换周期", "Switch interval"), 12, TextSecondary);
        intervalLabel.VerticalAlignment = VerticalAlignment.Center; intervalLabel.Margin = new Thickness(0, 0, 8, 0);
        intervalControls.Children.Add(intervalLabel); intervalControls.Children.Add(intervalChoice);
        Grid.SetColumn(intervalControls, 1); summaryRow.Children.Add(intervalControls);
        var selector = new ComboBox { Style = (Style)FindResource("SettingsCombo"), MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 6) };
        AutomationProperties.SetName(selector, Loc.T("胶囊切换目标方案", "Workspace for market clicks"));
        // Keep the configured target visible while the live catalog is unavailable.
        // This is display-only; a cached target never approves a layout or permits chart writes.
        if (savedWorkspace.Length > 0)
        {
            var cached = new ChartWorkspace(savedWorkspace, S.MarketChartWorkspaces.GetValueOrDefault(savedWorkspace)?.Name ?? "");
            selector.ItemsSource = new[] { cached }; selector.SelectedItem = cached;
            status.Text = Loc.T("显示已保存的目标方案，正在等待连接验证。", "Showing the saved target; waiting to verify the connection.");
        }
        var layoutHost = new StackPanel();
        var token = new PasswordBox { MaxLength = 4096, FontSize = 12.5, Padding = new Thickness(6), Background = Brushes.Transparent,
            Foreground = UI.Brush(TextPrimary), BorderBrush = UI.Brush(TextTertiary), Margin = new Thickness(0, 4, 0, 8) };
        AutomationProperties.SetName(token, Loc.T("OpenMarket 图表控制令牌", "OpenMarket chart-control token"));
        ChartLayout? layout = null;
        ChartWorkspace? selected = null;
        List<int> pinned = [];
        string selectedInterval = ChartIntervals.Default;
        bool filling = false, busy = false, fillingInterval = false;
        bool active = false, retryPending = false;
        var retry = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(15) };
        var buttons = new List<Button>();
        Button? confirm = null;
        bool Approved() => layout is not null && selected is not null && S.MarketChartWorkspaces.TryGetValue(selected.Id, out var saved)
            && saved.AcceptsLayout(layout);
        void UpdateSummary()
        {
            intervalChoice.IsEnabled = !busy && layout is not null && selected is not null;
            if (layout is null || selected is null) { summary.Text = ""; rotationSummary.Text = ""; if (confirm is not null) confirm.IsEnabled = false; return; }
            string keep = string.Join("、", pinned.Order().Select(index => index + 1));
            string rotate = string.Join("、", layout.Panes.Where(p => !pinned.Contains(p.Index)).Select(p => p.Index + 1));
            summary.Text = Loc.T($"当前方案：{selected.Name} · 共 {layout.Panes.Count} 图", $"Workspace: {selected.Name} · {layout.Panes.Count} charts");
            rotationSummary.Text = Loc.T($"保留：{(keep.Length == 0 ? "无" : keep)} · 轮换：{(rotate.Length == 0 ? "已全部固定，暂停切换" : rotate)}",
                $"Pinned: {(keep.Length == 0 ? "none" : keep)} · Rotation: {(rotate.Length == 0 ? "all pinned; paused" : rotate)}");
            if (confirm is not null) confirm.IsEnabled = !busy && !Approved();
        }
        void SavePins()
        {
            if (layout is null || selected is null) return;
            var value = new ChartRotationSettings { Name = selected.Name, LayoutSignature = layout.Signature, Interval = selectedInterval, PinnedPanes = pinned.Order().ToList() };
            Change(s => s.MarketChartWorkspaces[selected.Id] = value);
            status.Text = Loc.T("布局已确认，固定设置自动保存。", "Layout confirmed; pin changes save automatically."); UpdateSummary();
        }
        intervalChoice.SelectionChanged += (_, _) =>
        {
            if (fillingInterval || busy || layout is null || selected is null || intervalChoice.SelectedIndex < 0) return;
            selectedInterval = intervals[intervalChoice.SelectedIndex];
            if (Approved())
            {
                SavePins();
                status.Text = Loc.T("周期已自动保存，下次轮到未固定的图表时生效。", "Interval saved; it applies when an unpinned chart is next switched.");
            }
            else status.Text = Loc.T("已选择周期，确认布局后保存并生效。", "Interval selected; confirm the layout to save it.");
        };
        async Task Run(Func<Task> operation)
        {
            if (busy) return;
            retry.Stop(); retryPending = false;
            busy = true; selector.IsEnabled = false; layoutHost.IsEnabled = false;
            intervalChoice.IsEnabled = false;
            foreach (var button in buttons) button.IsEnabled = false;
            try { await operation(); }
            catch (ChartConnectionException ex) when (ex.IsTransient)
            {
                retryPending = true;
                status.Text = ex.Message + Loc.T("；将自动重试，无需手动刷新。", "; will retry automatically; no manual refresh is needed.");
                if (active) retry.Start();
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
                or System.Net.Http.HttpRequestException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException or OperationCanceledException)
            { status.Text = ex is OperationCanceledException ? Loc.T("方案读取已取消。", "Workspace reading cancelled.") : ex.Message; }
            finally
            {
                busy = false; selector.IsEnabled = true; layoutHost.IsEnabled = true;
                foreach (var button in buttons) button.IsEnabled = true;
                UpdateSummary();
            }
        }
        async Task Detect(ChartWorkspace workspace)
        {
            layout = null; selected = workspace; layoutHost.Children.Clear(); UpdateSummary();
            status.Text = Loc.T($"正在读取「{workspace.Name}」的布局…", $"Reading '{workspace.Name}'…");
            var detected = await _app.SelectMarketChartWorkspaceAsync(workspace);
            // A new shape never inherits old pin indices until the user confirms them.
            bool hasSaved = S.MarketChartWorkspaces.TryGetValue(workspace.Id, out var saved);
            selectedInterval = ChartIntervals.Normalize(saved?.Interval);
            fillingInterval = true;
            try { intervalChoice.SelectedIndex = Array.IndexOf(intervals, selectedInterval); }
            finally { fillingInterval = false; }
            layout = detected;
            pinned = hasSaved && saved!.AcceptsLayout(layout) ? [.. saved.PinnedPanes]
                : !hasSaved && workspace.Id == legacyWorkspace && layout.IsLegacyFourPane ? [0, .. S.MarketChartPinnedPanes] : [];
            layoutHost.Children.Add(MarketChartLayoutPreview(layout, pinned, () => { if (Approved()) SavePins(); else UpdateSummary(); }));
            status.Text = Approved() ? Loc.T("已读取布局；点击图表可固定或取消固定，设置自动保存。", "Layout read; click charts to pin or unpin. Changes save automatically.")
                : Loc.T("请确认各图位置和固定设置，然后点击“确认布局并启用”。", "Review chart positions and pins, then confirm the layout below.");
            if (layout.Panes.Count > 1 && (layout.SymbolSync || layout.IntervalSync))
                status.Text += Loc.T(" 请在网页关闭币种同步和周期同步后再切换。", " Turn off symbol and interval sync on the page before switching.");
            UpdateSummary();
        }
        async Task Refresh()
        {
            status.Text = Loc.T("正在读取方案列表…", "Reading workspace list…");
            var workspaces = await _app.ListMarketChartWorkspacesAsync();
            string id = _app.ChartConnection.WorkspaceId;
            filling = true;
            try { selector.ItemsSource = workspaces; selector.SelectedItem = workspaces.FirstOrDefault(item => item.Id == id); }
            finally { filling = false; }
            layout = null; selected = null; layoutHost.Children.Clear(); UpdateSummary();
            if (selector.SelectedItem is ChartWorkspace workspace) await Detect(workspace);
            else status.Text = workspaces.Count == 0 ? Loc.T("账号中没有图表方案，请先在网页创建。", "No workspaces found; create one on the website first.")
                : Loc.T("请选择胶囊切换使用的方案。原目标若已删除，将暂停切换。", "Select a workspace for market clicks. A deleted target will not be switched.");
        }
        retry.Tick += async (_, _) =>
        {
            retry.Stop();
            if (active && retryPending) await Run(Refresh);
        };
        panel.Unloaded += (_, _) => { active = false; retry.Stop(); };
        selector.SelectionChanged += async (_, _) => { if (!filling && selector.SelectedItem is ChartWorkspace workspace) await Run(() => Detect(workspace)); };
        panel.Children.Add(TextLine(Loc.T("图表控制令牌（已配置时留空可保留）", "Chart-control token (leave blank to keep saved token)"), 12, TextSecondary));
        panel.Children.Add(token);
        var connect = ActionButton(Loc.T("保存授权并读取方案", "Save authorization and load workspaces"), async () => await Run(async () =>
        {
            _app.ConfigureMarketChart(_app.ChartConnection.WorkspaceId, token.Password); token.Clear(); await Refresh();
        })); buttons.Add(connect); panel.Children.Add(connect);
        panel.Children.Add(TextLine(Loc.T("胶囊切换目标方案", "Workspace for market clicks"), 12, TextSecondary)); panel.Children.Add(selector);
        var refresh = ActionButton(Loc.T("刷新方案及布局", "Refresh workspaces and layout"), async () => await Run(Refresh));
        buttons.Add(refresh); panel.Children.Add(refresh); panel.Children.Add(summaryRow); panel.Children.Add(rotationSummary); panel.Children.Add(layoutHost);
        confirm = ActionButton(Loc.T("确认布局并启用", "Confirm layout and enable"), SavePins); confirm.IsEnabled = false; buttons.Add(confirm); panel.Children.Add(confirm);
        panel.Children.Add(status);
        panel.Children.Add(TextLine(Loc.T("没有固定图表时自动跟随最新布局；有固定图表时，布局数量、排列或图表身份变化后需重新检测并确认。全部固定时暂停切换。收盘倒计时的 BTC&ETH 联动独立运行。", "Without pinned charts, switching follows the live layout. With pins, changes to the chart count, arrangement or identity require reconfirmation. Pinning all charts pauses rotation. The BTC&ETH candle-close action remains independent."), 11, TextSecondary));
        var extensionHint = TextLine(Loc.T("首次使用需在 Chrome 扩展管理中开启开发者模式，加载程序目录中的 chrome-extension 文件夹。", "First, enable Developer mode in Chrome extensions and load this app's chrome-extension folder."), 11, TextSecondary);
        extensionHint.Margin = new Thickness(0, 8, 0, 4); panel.Children.Add(extensionHint);
        panel.Children.Add(ActionButton(Loc.T("打开 Chrome 扩展文件夹", "Open Chrome extension folder"), () =>
        {
            try
            {
                Native.ChromeChartBridge.RegisterHost(_app.Store.DirectoryPath);
                string folder = System.IO.Path.Combine(AppContext.BaseDirectory, "chrome-extension");
                if (!System.IO.Directory.Exists(folder)) throw new System.IO.IOException(Loc.T("请使用完整便携版。", "Use the complete portable build."));
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Security.SecurityException) { status.Text = ex.Message; }
        }));
        panel.Children.Add(Link(Loc.T("管理 OpenMarket 图表授权 ↗", "Manage OpenMarket chart authorization ↗"), "https://openmarket.xyz/chart/console/agents"));
        bool loaded = false;
        panel.Loaded += async (_, _) =>
        {
            active = true;
            if (loaded) { if (retryPending) retry.Start(); return; }
            loaded = true;
            await Run(async () =>
            {
                if (_app.ChartConnection.Token.Length > 0) await Refresh();
                else status.Text = Loc.T("请先填写并保存图表控制令牌。", "Enter and save a chart-control token first.");
            });
        };
    }
}
