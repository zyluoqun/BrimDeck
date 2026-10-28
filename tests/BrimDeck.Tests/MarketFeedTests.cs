using BrimDeck.Core;

internal static class MarketFeedTests
{
    public static async Task Run(Action<string, bool> check)
    {
        const string json = """
            {"at":2000,"snapshotAt":1000,"hot":[{"asset":"PEPE","symbol":"1000PEPEUSDT","perpetual":true,"change24h":2.35,"quoteAt":2000}],
             "gainers":[{"asset":"UP","symbol":"UPUSDT","perpetual":true,"change24h":5,"quoteAt":2000}],
             "losers":[{"asset":"DOWN","symbol":"DOWNUSDT","perpetual":true,"change24h":null}],"error":null,"hotError":null}
            """;
        var snapshot = MarketFeedSnapshot.Parse(json);
        check("Countdown calibration uses current response time instead of ranking snapshot time", snapshot.ServerTime == 2000 && snapshot.SnapshotAt == 1000);
        check("Market feed preserves actual contract symbols and selected ranking", snapshot.Rows(MarketRanking.Hot).Single().Symbol == "1000PEPEUSDT" && snapshot.Rows(MarketRanking.Gainers).Single().Asset == "UP");
        check("Missing market changes stay missing", snapshot.Rows(MarketRanking.Losers).Single().Change24h is null);
        check("Market links preserve spot and futures identity", MarketCoin.ChartUrl("1000PEPEUSDT", true).Contains("BINANCE_FUTURES%3A1000PEPEUSDT") && MarketCoin.ChartUrl("OSMOUSDT", false).Contains("BINANCE%3AOSMOUSDT"));
        bool invalid = false;
        try { MarketCoin.ChartUrl("BTCUSDT&bad=1", true); } catch (ArgumentException) { invalid = true; }
        check("Market links reject invalid symbols", invalid);
        var later = DateTimeOffset.FromUnixTimeMilliseconds(70001);
        check("Old quotes are not displayed as current", snapshot.Rows(MarketRanking.Hot).Single().CurrentChange(later) is null);
        using var handler = new MarketHandler(json);
        using var http = new HttpClient(handler);
        var client = new MarketFeedClient(http, new("https://example.com/api/companion/market", "read-key"));
        await client.ReadAsync(CancellationToken.None);
        check("Market client uses scoped bearer authorization", handler.Authorization == "Bearer read-key");
        handler.Age = TimeSpan.FromMinutes(1);
        check("Cached responses cannot recalibrate the countdown", (await client.ReadAsync(CancellationToken.None)).ServerTime is null);
        handler.Age = null; handler.Json = json.Replace("\"at\":2000,", "");
        check("Ranking timestamps never serve as a countdown clock", (await client.ReadAsync(CancellationToken.None)).ServerTime is null);
        handler.Date = DateTimeOffset.FromUnixTimeMilliseconds(3000);
        check("An HTTP response date can calibrate feeds without generation time", (await client.ReadAsync(CancellationToken.None)).ServerTime == 3000);
        check("Market client refuses plaintext transport", !MarketFeedConfiguration.ValidEndpoint("http://example.com/api/companion/market"));
        check("LAN HTTP requires explicit opt-in and a private IP", !MarketFeedConfiguration.ValidEndpoint("http://192.168.0.211:5089/api/companion/market")
            && MarketFeedConfiguration.ValidEndpoint("http://192.168.0.211:5089/api/companion/market", true)
            && !MarketFeedConfiguration.ValidEndpoint("http://8.8.8.8/market", true));
        var settings = new DeckSettings { CapsuleMarket = true, MarketRanking = MarketRanking.Losers };
        settings.Normalize();
        check("Market selection is retained by settings normalization", settings.CapsuleMarket && settings.MarketRanking == MarketRanking.Losers);
    }
    private sealed class MarketHandler(string json) : HttpMessageHandler
    {
        public string Json = json;
        public TimeSpan? Age;
        public DateTimeOffset? Date;
        public string? Authorization;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(Json) };
            response.Headers.Age = Age; response.Headers.Date = Date;
            return Task.FromResult(response);
        }
    }
}
