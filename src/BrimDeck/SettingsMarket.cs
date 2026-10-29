using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    private FrameworkElement MarketRankingChoice()
    {
        var panel = new StackPanel();
        panel.Children.Add(TextLine(Loc.T("行情榜单（可多选）", "Market rankings (select multiple)"), 13, TextPrimary, FontWeights.SemiBold));
        var hint = TextLine(Loc.T("按下列顺序滚动显示，每个榜单独立设置 1–50 个币种，默认 20 个。", "Scrolls in the order below. Each board shows 1–50 coins, with 20 by default."), 11, TextSecondary);
        hint.TextWrapping = TextWrapping.Wrap; hint.Margin = new Thickness(0, 4, 0, 4); panel.Children.Add(hint);
        foreach (var ranking in MarketBoards.Order)
        {
            string title = MarketPageView.Label(ranking);
            var box = new CheckBox { Style = (Style)FindResource("SettingsToggle"), IsChecked = S.SelectedMarketRankings.Contains(ranking),
                Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(box, Loc.T("行情榜单 ", "Market ranking ") + title);
            void Select(bool on) => Change(s =>
            {
                s.MarketRankings = s.SelectedMarketRankings.Where(value => value != ranking).ToList();
                if (on) s.MarketRankings.Add(ranking);
            });
            box.Checked += (_, _) => Select(true); box.Unchecked += (_, _) => Select(false);
            var controls = NumericControls(Loc.T(title + "显示数量", title + " coin count"), S.MarketCount(ranking), 1, 50, 1, Loc.T("个", "coins"),
                value => Change(s => s.MarketCounts[ranking] = (int)value));
            var row = new Grid { Height = 44 };
            row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new());
            var label = TextLine(title, 13, TextPrimary); label.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(label); Grid.SetColumn(controls.Number, 1); row.Children.Add(controls.Number);
            Grid.SetColumn(box, 2); row.Children.Add(box); panel.Children.Add(row);
        }
        return panel;
    }
    private void MarketSizeControls()
    {
        var dimensions = new Grid { Margin = new Thickness(0, 8, 0, 0) }; dimensions.ColumnDefinitions.Add(new()); dimensions.ColumnDefinitions.Add(new());
        var width = TextLine(Loc.T("宽度随行情内容自动调整", "Width automatically fits the market content"), 12, TextSecondary);
        width.Margin = new Thickness(0, 0, 20, 0); dimensions.Children.Add(width);
        var height = Dimension(Loc.T("高度", "Height"), S.MarketHeight, DeckPages.MinHeight, DeckPages.MaxHeight,
            value => Resize(s => s.MarketHeight = value, DeckPage.Market)); height.Margin = new Thickness(20, 0, 0, 0); Grid.SetColumn(height, 1); dimensions.Children.Add(height);
        Target.Children.Add(dimensions);
        Target.Children.Add(TextLine(Loc.T("展开页与收起摘要独立设置。刘海、胶囊和指标条的摘要开关在收起外观中。", "Expanded pages and compact summaries are independent. Configure the notch, capsule and indicator summaries under collapsed appearance."), 11, TextSecondary));
        MarketChartControls();
    }
    private void MarketChartControls()
    {
        var heading = TextLine(Loc.T("网页图表联动", "Browser chart connection"), 13, TextPrimary, FontWeights.SemiBold);
        heading.Margin = new Thickness(0, 16, 0, 8); Target.Children.Add(heading);
        var hint = TextLine(Loc.T("点击行情会找到或打开独立的四屏 Chrome 窗口，将其移到 2 号屏并进入 F11 全屏，再依次切换下方未固定的窗口，周期统一为 4h。", "Market clicks open or activate a dedicated Chrome chart window, enter fullscreen on display 2, then rotate through the unpinned lower panes at 4h."), 11, TextSecondary);
        hint.TextWrapping = TextWrapping.Wrap; Target.Children.Add(hint);
        var pinHint = TextLine(Loc.T("下方从左到右为窗口 1、2、3。最多固定两个；固定后保留当前币种，不参与切换。设置自动保存，对下一次切换生效。", "The lower panes are 1, 2 and 3 from left to right. Pin up to two to keep their current markets out of the rotation. Changes save automatically and apply to the next switch."), 11, TextSecondary);
        pinHint.TextWrapping = TextWrapping.Wrap; pinHint.Margin = new Thickness(0, 8, 0, 4); Target.Children.Add(pinHint);
        var switches = new List<(CheckBox Box, int Pane)>();
        void UpdatePins()
        {
            foreach (var (box, pane) in switches)
                box.IsEnabled = S.MarketChartPinnedPanes.Contains(pane) || S.MarketChartPinnedPanes.Count < 2;
        }
        foreach (int pane in Enumerable.Range(1, 3))
        {
            string position = pane switch { 1 => Loc.T("左", "left"), 2 => Loc.T("中", "middle"), _ => Loc.T("右", "right") };
            var row = Toggle(Loc.T($"固定窗口 {pane}（{position}）", $"Pin pane {pane} ({position})"), S.MarketChartPinnedPanes.Contains(pane), on =>
            {
                Change(s =>
                {
                    s.MarketChartPinnedPanes.Remove(pane);
                    if (on && s.MarketChartPinnedPanes.Count < 2) s.MarketChartPinnedPanes.Add(pane);
                });
                UpdatePins();
            });
            if (row is Border { Child: CheckBox box }) switches.Add((box, pane));
            Target.Children.Add(row);
        }
        UpdatePins();
        string saved = "";
        try { saved = _app.ChartConnection.WorkspaceId; }
        catch (Exception ex) when (ex is System.IO.IOException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException or ArgumentException) { }
        Target.Children.Add(TextLine(Loc.T("图表地址或布局 ID", "Chart URL or workspace ID"), 12, TextSecondary));
        var workspace = new TextBox { Style = (Style)FindResource("SettingsField"), MaxLength = 240, Text = saved,
            Margin = new Thickness(0, 4, 0, 8) };
        System.Windows.Automation.AutomationProperties.SetName(workspace, Loc.T("OpenMarket 图表地址", "OpenMarket chart URL")); Target.Children.Add(workspace);
        Target.Children.Add(TextLine(Loc.T("图表控制令牌（已配置时留空可保留）", "Chart-control token (leave blank to keep the saved token)"), 12, TextSecondary));
        var token = new PasswordBox { MaxLength = 4096, FontSize = 12.5, Padding = new Thickness(6), Background = Brushes.Transparent,
            Foreground = UI.Brush(TextPrimary), BorderBrush = UI.Brush(TextTertiary), Margin = new Thickness(0, 4, 0, 8) };
        System.Windows.Automation.AutomationProperties.SetName(token, Loc.T("OpenMarket 图表控制令牌", "OpenMarket chart-control token")); Target.Children.Add(token);
        var status = TextLine(saved.Length > 0 ? Loc.T("已配置，令牌已加密保存在本机", "Configured; the token is encrypted on this PC") : Loc.T("尚未配置图表联动", "Chart connection is not configured"), 11, TextSecondary);
        var save = ActionButton(Loc.T("保存图表联动", "Save chart connection"), () =>
        {
            try
            {
                _app.ConfigureMarketChart(workspace.Text, token.Password); token.Clear(); workspace.Text = _app.ChartConnection.WorkspaceId;
                status.Text = Loc.T("已保存 · 下次点击从下方首个未固定窗口开始 · 默认 4h", "Saved · next click starts at the first unpinned lower pane · default 4h");
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
            { status.Text = ex.Message; }
        });
        Target.Children.Add(save); Target.Children.Add(status);
        var extensionHint = TextLine(Loc.T("首次使用需在 Chrome 扩展管理中开启开发者模式，加载程序目录中的 chrome-extension 文件夹。扩展仅用于 OpenMarket 窗口联动。", "First, enable Developer mode in Chrome extensions and load this app's chrome-extension folder. The extension only connects the OpenMarket chart window."), 11, TextSecondary);
        extensionHint.TextWrapping = TextWrapping.Wrap; extensionHint.Margin = new Thickness(0, 8, 0, 4); Target.Children.Add(extensionHint);
        Target.Children.Add(ActionButton(Loc.T("打开 Chrome 扩展文件夹", "Open Chrome extension folder"), () =>
        {
            try
            {
                Native.ChromeChartBridge.RegisterHost(_app.Store.DirectoryPath);
                string folder = System.IO.Path.Combine(AppContext.BaseDirectory, "chrome-extension");
                if (!System.IO.Directory.Exists(folder)) throw new System.IO.IOException(Loc.T("请使用包含 chrome-extension 文件夹的完整便携版", "Use the complete portable build with its chrome-extension folder"));
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Security.SecurityException) { status.Text = ex.Message; }
        }));
        Target.Children.Add(Link(Loc.T("管理 OpenMarket 图表授权 ↗", "Manage OpenMarket chart authorization ↗"), "https://openmarket.xyz/chart/console/agents"));
    }
}
