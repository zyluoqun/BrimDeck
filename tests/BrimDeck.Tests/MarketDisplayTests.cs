using System.Text.Json;
using BrimDeck.Core;

internal static class MarketDisplayTests
{
    public static void Run(Action<string, bool> check)
    {
        var settings = new DeckSettings();
        check("Each of the four market boards defaults to twenty coins", MarketBoards.Order.Count == 4 && MarketBoards.Order.All(ranking => settings.MarketCount(ranking) == 20));
        var legacy = SettingsMigrations.Read("{\"MarketRanking\":2}");
        check("Older single-board settings keep their selected ranking", legacy.SelectedMarketRankings.SequenceEqual([MarketRanking.Losers]));
        settings.MarketRankings = [MarketRanking.Losers, MarketRanking.Hot, MarketRanking.Pool, MarketRanking.Gainers];
        settings.MarketCounts = new() { [MarketRanking.Pool] = 5, [MarketRanking.Hot] = 10, [MarketRanking.Gainers] = 2, [MarketRanking.Losers] = 1 };
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var coins = Enumerable.Range(1, 30).Select(index => new MarketCoin("COIN" + index, "COIN" + index + "USDT", true, index, now)).ToArray();
        var snapshot = new MarketFeedSnapshot(now, coins, coins, coins, null, null) { Pool = coins };
        var rows = snapshot.DisplayRows(settings);
        check("Four boards display together in pool, hot, gainers, losers order", rows.Select(row => row.Ranking).Distinct().SequenceEqual(MarketBoards.Order));
        check("Every board independently limits its displayed coins", rows.Count == 18 && MarketBoards.Order.All(ranking => rows.Count(row => row.Ranking == ranking) == settings.MarketCount(ranking)));
        check("Shared symbols retain each board's membership and original ranking", rows.Count(row => row.Coin.Symbol == "COIN1USDT") == 4
            && MarketBoards.Order.All(ranking => rows.Where(row => row.Ranking == ranking).Select(row => row.Rank).SequenceEqual(Enumerable.Range(1, settings.MarketCount(ranking)))));
        var restored = SettingsMigrations.Read(JsonSerializer.Serialize(settings));
        check("Multi-selection and independent counts survive reload", restored.SelectedMarketRankings.SequenceEqual(MarketBoards.Order)
            && MarketBoards.Order.All(ranking => restored.MarketCount(ranking) == settings.MarketCount(ranking)));
        var copy = settings.Copy(); copy.MarketRankings!.Clear(); copy.MarketCounts[MarketRanking.Hot] = 1;
        check("Editing a settings copy cannot change the original board choices or counts", settings.SelectedMarketRankings.Count == 4 && settings.MarketCount(MarketRanking.Hot) == 10);
        settings.MarketRankings = []; settings.Normalize();
        check("Unchecking every board survives normalization and reload", settings.SelectedMarketRankings.Count == 0 && snapshot.DisplayRows(settings).Count == 0
            && SettingsMigrations.Read(JsonSerializer.Serialize(settings)).SelectedMarketRankings.Count == 0);
        settings.MarketRankings = [MarketRanking.Pool, MarketRanking.Hot, MarketRanking.Hot, (MarketRanking)999];
        settings.MarketCounts = new() { [MarketRanking.Hot] = 0, [MarketRanking.Pool] = 999, [(MarketRanking)999] = 5 }; settings.Normalize();
        check("Invalid duplicate boards and out-of-range counts normalize safely", settings.SelectedMarketRankings.SequenceEqual([MarketRanking.Pool, MarketRanking.Hot])
            && settings.MarketCount(MarketRanking.Hot) == 1 && settings.MarketCount(MarketRanking.Pool) == 50 && settings.MarketCounts.Count == 2);
        check("A shorter observation pool displays only the coins actually available", snapshot.DisplayRows(settings).Count(row => row.Ranking == MarketRanking.Pool) == coins.Length);
        settings.MarketRankings = [.. MarketBoards.Order]; settings.MarketCounts = [];
        check("An unavailable hot board does not hide the other selected boards", (snapshot with { HotError = "hot offline" }).DisplayRows(settings).Count == 60);
        check("An observation pool remains available without a ranking snapshot", (snapshot with { Error = "no ranking snapshot" }).DisplayRows(settings).Count == 20);
        check("An unavailable pool does not hide the original rankings", (snapshot with { PoolError = "pool offline" }).DisplayRows(settings).Count == 60);
        restored = SettingsMigrations.Read("{\"MarketRankings\":null,\"MarketCounts\":null,\"MarketRanking\":1}");
        check("Null market options safely recover the legacy choice and default counts", restored.SelectedMarketRankings.SequenceEqual([MarketRanking.Gainers]) && restored.MarketCount(MarketRanking.Pool) == 20);
    }
}
