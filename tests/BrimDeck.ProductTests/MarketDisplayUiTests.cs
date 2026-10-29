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
        check("The real ticker shows all four board headings in order", row.Children.OfType<TextBlock>().Select(label => label.Text).SequenceEqual(MarketBoards.Order.Select(MarketPageView.Label)));
        var buttons = row.Children.OfType<Button>().ToArray();
        check("The real ticker respects four independent display counts", buttons.Length == 18);
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
                var controls = (StackPanel)typeof(SettingsWindow).GetMethod("MarketRankingChoice", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null)!;
                var rows = controls.Children.OfType<Grid>().ToArray();
                var boxes = rows.Select(grid => grid.Children.OfType<CheckBox>().Single()).ToArray();
                check("Market settings contain four independent checkboxes", boxes.Length == 4 && boxes.All(box => box.IsChecked == true)
                    && boxes.Select(box => box.Content?.ToString()).SequenceEqual(MarketBoards.Order.Select(MarketPageView.Label)));
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
