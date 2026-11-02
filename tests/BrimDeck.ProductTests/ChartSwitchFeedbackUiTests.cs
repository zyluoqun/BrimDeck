using System.IO;
using BrimDeck.Core;

internal static class ChartSwitchFeedbackUiTests
{
    internal static void Run(BrimDeck.App app, Action<string, bool> check)
    {
        var storeProperty = typeof(BrimDeck.App).GetProperty(nameof(BrimDeck.App.Store))!;
        var store = app.Store;
        string folder = Path.Combine(Path.GetTempPath(), "BrimDeck-Chart-Feedback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var notices = new List<string>();
        int stopped = 0;
        void Notice(string text)
        {
            notices.Add(text);
            if (app.PendingChartCoins.Count == 0) stopped++;
        }
        try
        {
            storeProperty.SetValue(app, new SettingsStore(folder));
            var coin = new MarketCoin("BTC", "BTCUSDT", true, null, null);
            app.SwitchMarketChartAsync(coin, Notice).GetAwaiter().GetResult();
            app.SwitchMarketChartAsync(coin, Notice).GetAwaiter().GetResult();
            check("Repeated failed manual clicks each display their reason without a silent cooldown", notices.Count == 2 && notices[0] == notices[1]);
            check("Every failure clears loading before presenting its reason", stopped == 2 && app.PendingChartCoins.Count == 0 && app.MarketChartStatus == notices[1]);
        }
        finally
        {
            storeProperty.SetValue(app, store);
            Directory.Delete(folder, true);
        }
    }
}
