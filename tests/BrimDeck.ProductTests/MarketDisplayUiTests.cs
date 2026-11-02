using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using BrimDeck;
using BrimDeck.Core;

internal static class MarketDisplayUiTests
{
    public static void Run(BrimDeck.App app, Action<string, bool> check)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var coins = Enumerable.Range(1, 30).Select(index => new MarketCoin("COIN" + index, "COIN" + index + "USDT", true, index, now)).ToArray();
        var snapshot = new MarketFeedSnapshot(now, coins, coins, coins, null, null) { Pool = coins };
        var settings = new DeckSettings { MarketRankings = [.. MarketBoards.Order], MarketCounts = new()
            { [MarketRanking.Pool] = 5, [MarketRanking.Hot] = 10, [MarketRanking.Gainers] = 2, [MarketRanking.Losers] = 1 } };
        MarketCoin? clicked = null;
        var ticker = new MarketTickerStrip(4000, 24, 1, false, switchChart: coin => { clicked = coin; return Task.CompletedTask; });
        ticker.Update(snapshot, settings, null);
        var row = (StackPanel)((Canvas)ticker.Children[0]).Children[0];
        check("The ticker replaces board headings with per-coin icons", !row.Children.OfType<TextBlock>().Any());
        var buttons = row.Children.OfType<Button>().ToArray();
        check("The real ticker respects four independent display counts", buttons.Length == 18);
        FrameworkElement Icon(Button button) => (FrameworkElement)((InlineUIContainer)((TextBlock)button.Content).Inlines.ElementAt(1)).Child;
        var expectedBoards = new[] { (MarketRanking.Pool, 5), (MarketRanking.Hot, 10), (MarketRanking.Gainers, 2), (MarketRanking.Losers, 1) }
            .SelectMany(board => Enumerable.Repeat(MarketPageView.Label(board.Item1), board.Item2));
        check("Every coin identifies its board, including the observation pool", buttons.Select(button => AutomationProperties.GetName(Icon(button))).SequenceEqual(expectedBoards));
        var icons = buttons.Select(Icon).ToArray();
        check("All four board icons have distinct shapes and the same reserved size", icons.Cast<System.Windows.Shapes.Path>().Select(icon => icon.Data.ToString()).Distinct().Count() == 4
            && icons.All(icon => icon.Width + icon.Margin.Left + icon.Margin.Right == 16));
        check("Gainer and loser arrows use the approved smaller, thinner preview", icons.Skip(15).Cast<System.Windows.Shapes.Path>()
            .All(icon => icon.Width == 8 && icon.Height == 8 && icon.StrokeThickness == 1 && icon.Margin.Left == 2 && icon.Margin.Right == 6));
        check("Gainer and loser markers use vertical arrow proportions", icons.Skip(15).Cast<System.Windows.Shapes.Path>()
            .All(icon => icon.Data.Bounds.Height > icon.Data.Bounds.Width));
        check("Observation pool and hot icons retain their original size", icons.Take(15).Cast<System.Windows.Shapes.Path>()
            .All(icon => icon.Width == 12 && icon.Height == 12 && icon.StrokeThickness == 1.3 && icon.Margin.Left == 0 && icon.Margin.Right == 4));
        string Rank(Button button) => ((TextBlock)button.Content).Inlines.OfType<Run>().First().Text;
        check("Ticker ranking restarts for every selected board", buttons.Select(Rank).SequenceEqual(new[] { 5, 10, 2, 1 }.SelectMany(count => Enumerable.Range(1, count).Select(index => index + "."))));
        buttons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check("Observation pool coins use the shared chart click handler", clicked?.Symbol == coins[0].Symbol && clicked.Perpetual);
        ticker.SetPending([coins[0]]);
        check("A shared coin shows its loading ring in each selected board", buttons.Count(button => ((TextBlock)button.Content).Inlines.OfType<InlineUIContainer>().Any(inline => inline.Child is MarketLoadingRing { IsLoading: true })) == 4);
        ticker.SetPending([]);
        ticker.Update(snapshot with { Hot = coins.Select(coin => coin with { Change24h = 42 }).ToArray() }, settings, null);
        check("Duplicate symbols keep the quote from their own board", ((TextBlock)buttons[0].Content).Inlines.OfType<Run>().Last().Text == "+1.00%"
            && ((TextBlock)buttons[5].Content).Inlines.OfType<Run>().Last().Text == "+42.00%");
        settings.MarketRankings = [MarketRanking.Pool]; ticker.Update(snapshot, settings, null);
        check("Unchecking boards immediately removes their coins", row.Children.OfType<Button>().Count() == 5 && !row.Children.OfType<TextBlock>().Any());
        check("A single selected board still shows its icon on every coin", row.Children.OfType<Button>().All(button => AutomationProperties.GetName(Icon(button)) == MarketPageView.Label(MarketRanking.Pool)));
        settings.MarketRankings = []; ticker.Update(snapshot, settings, null);
        check("An empty selection has an explicit state without coin buttons", !row.Children.OfType<Button>().Any() && row.Children.OfType<TextBlock>().Single().Text.Length > 0);
        ticker.Stop();
        var view = new MarketPageView(_ => { }, () => { });
        view.Update(snapshot, MarketRanking.Pool, null, count: 5);
        check("The expanded observation pool applies its configured display count", view.RowCount == 5 && view.Ranking == MarketRanking.Pool);
        view.Update(snapshot with { Error = "no ranking snapshot" }, MarketRanking.Pool, null, count: 5);
        check("An unrelated ranking error cannot hide the expanded observation pool", view.RowCount == 5);
        view.Update(snapshot, MarketRanking.Hot, null, count: 10);
        check("The expanded hot board independently applies its configured display count", view.RowCount == 10);

        var saved = app.Settings.Copy();
        try
        {
            app.Settings.MarketRankings = [.. MarketBoards.Order]; app.Settings.MarketCounts = [];
            var window = new SettingsWindow(app);
            try
            {
                var target = (Panel)typeof(SettingsWindow).GetProperty("Target", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                int before = target.Children.Count;
                typeof(SettingsWindow).GetMethod("MarketChartControls", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
                var workspaceControls = target.Children.Cast<DependencyObject>().Skip(before).SelectMany(Descendants).ToArray();
                check("Workspace settings expose a named selector instead of a manually typed chart id", workspaceControls.OfType<ComboBox>()
                    .Count(box => AutomationProperties.GetName(box) == Loc.T("胶囊切换目标方案", "Workspace for market clicks")) == 1);
                check("Layout approval stays disabled until a workspace is detected", workspaceControls.OfType<Button>()
                    .Single(button => button.Content as string == Loc.T("确认布局并启用", "Confirm layout and enable")).IsEnabled == false);
                var intervalChoice = workspaceControls.OfType<ComboBox>().Single(box => AutomationProperties.GetName(box) == Loc.T("方案切换周期", "Workspace switch interval"));
                check("Workspace interval choices default to four hours and wait for a detected workspace", !intervalChoice.IsEnabled
                    && intervalChoice.SelectedItem as string == ChartIntervals.Label("4h") && intervalChoice.Items.Count == ChartIntervals.All.Count
                    && ChartIntervals.Label("1m") != ChartIntervals.Label("1M"));
                var intervalControls = (StackPanel)intervalChoice.Parent;
                var summaryRow = (Grid)intervalControls.Parent;
                summaryRow.Measure(new Size(600, double.PositiveInfinity)); summaryRow.Arrange(new Rect(0, 0, 600, summaryRow.DesiredSize.Height)); summaryRow.UpdateLayout();
                check("The interval selector is right-aligned beside the current workspace summary", Grid.GetColumn(intervalControls) == 1
                    && summaryRow.Children.OfType<TextBlock>().Single() is { } currentWorkspace && Grid.GetColumn(currentWorkspace) == 0
                    && Math.Abs(intervalChoice.TranslatePoint(new Point(intervalChoice.ActualWidth, 0), summaryRow).X - 600) < 1);
                foreach (int count in new[] { 1, 2, 3, 4, 5, 9 })
                {
                    var cells = Enumerable.Range(0, count).Select(i => new ChartPane(i, i, 0, 1, 1, "COIN" + i, "1h")).ToArray();
                    var layout = new ChartLayout("test", count, 1, cells, "signature", false, false);
                    var pinned = new List<int>(); int updates = 0;
                    var preview = (Grid)typeof(SettingsWindow).GetMethod("MarketChartLayoutPreview", BindingFlags.NonPublic | BindingFlags.Instance)!
                        .Invoke(window, [layout, pinned, (Action)(() => updates++)])!;
                    var paneButtons = preview.Children.OfType<Button>().ToArray();
                    check($"The real {count}-chart preview matches chart coordinates", paneButtons.Length == count
                        && paneButtons.Select(Grid.GetColumn).SequenceEqual(Enumerable.Range(0, count)));
                    foreach (var button in paneButtons) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    check($"All {count} preview charts can be pinned with an explicit state", pinned.SequenceEqual(Enumerable.Range(0, count))
                        && paneButtons.All(button => AutomationProperties.GetName(button).Contains(Loc.T("已固定", "Pinned"))) && updates == count);
                    paneButtons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    check("Clicking a pinned preview chart restores it to rotation", !pinned.Contains(0)
                        && AutomationProperties.GetName(paneButtons[0]).Contains(Loc.T("参与轮换", "Rotates")));
                }
                var controls = (StackPanel)typeof(SettingsWindow).GetMethod("MarketRankingChoice", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null)!;
                var rows = controls.Children.OfType<Grid>().ToArray();
                var boxes = rows.Select(grid => grid.Children.OfType<CheckBox>().Single()).ToArray();
                check("Market settings contain four independent checkboxes", boxes.Length == 4 && boxes.All(box => box.IsChecked == true)
                    && boxes.Select(AutomationProperties.GetName).SequenceEqual(MarketBoards.Order.Select(ranking => Loc.T("行情榜单 ", "Market ranking ") + MarketPageView.Label(ranking))));
                foreach (var grid in rows)
                {
                    grid.Measure(new Size(600, 44)); grid.Arrange(new Rect(0, 0, 600, 44)); grid.UpdateLayout();
                    var label = grid.Children.OfType<TextBlock>().Single();
                    var number = grid.Children.OfType<Border>().Single();
                    var toggle = grid.Children.OfType<CheckBox>().Single();
                    check("Board name, count and trailing switch occupy separate columns", Grid.GetColumn(label) == 0 && Grid.GetColumn(number) == 1 && Grid.GetColumn(toggle) == 2);
                    check("Board count is centered and its switch reaches the trailing edge", Math.Abs(number.TranslatePoint(new Point(number.ActualWidth / 2, 0), grid).X - 300) < 1
                        && Math.Abs(toggle.TranslatePoint(new Point(toggle.ActualWidth, 0), grid).X - 600) < 1);
                    check("Board selectors use the shared switch appearance", ReferenceEquals(toggle.Style, window.FindResource("SettingsToggle")));
                }
                check("Each board exposes a default twenty-coin numeric input", rows.All(grid => Descendants(grid).OfType<TextBox>().Single().Text == "20"));
                check("Board selectors have distinct accessible names", boxes.Select(AutomationProperties.GetName).Distinct().Count() == 4);
            }
            finally
            {
                // The earlier layout fixtures have closed their deck; closing this unshown settings fixture must not preview it.
                var exiting = typeof(BrimDeck.App).GetProperty(nameof(BrimDeck.App.Exiting))!;
                bool wasExiting = app.Exiting; exiting.SetValue(app, true);
                try { window.Close(); } finally { exiting.SetValue(app, wasExiting); }
            }
        }
        finally { typeof(BrimDeck.App).GetProperty(nameof(BrimDeck.App.Settings))!.SetValue(app, saved); }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
}
