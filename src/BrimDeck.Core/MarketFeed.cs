using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BrimDeck.Core;

public enum MarketRanking { Hot, Gainers, Losers }
public sealed record MarketCoin(string Asset, string Symbol, bool Perpetual, double? Change24h, long? QuoteAt)
{
    public double? CurrentChange(DateTimeOffset now) => QuoteAt is { } at && at >= now.ToUnixTimeMilliseconds() - 60000
        && at <= now.ToUnixTimeMilliseconds() + 30000 ? Change24h : null;
    public static string ChartUrl(string symbol, bool perpetual)
    {
        if (!Regex.IsMatch(symbol, @"^[\p{L}\p{N}]{1,40}USDT$")) throw new ArgumentException("Invalid market symbol.");
        return "https://openmarket.xyz/chart/?symbol=" + Uri.EscapeDataString((perpetual ? "BINANCE_FUTURES:" : "BINANCE:") + symbol) + "&interval=4h";
    }
}
public sealed record MarketFeedSnapshot(long? SnapshotAt, IReadOnlyList<MarketCoin> Hot, IReadOnlyList<MarketCoin> Gainers,
    IReadOnlyList<MarketCoin> Losers, string? Error, string? HotError)
{
    public long? ServerTime { get; init; }
    public static MarketFeedSnapshot Empty { get; } = new(null, [], [], [], null, null);
    public IReadOnlyList<MarketCoin> Rows(MarketRanking ranking) => ranking switch { MarketRanking.Gainers => Gainers, MarketRanking.Losers => Losers, _ => Hot };
    public static MarketFeedSnapshot Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string? Message(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        IReadOnlyList<MarketCoin> Read(string key)
        {
            if (!root.TryGetProperty(key, out var values) || values.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid market feed.");
            var rows = new List<MarketCoin>();
            foreach (var row in values.EnumerateArray().Take(50))
            {
                var asset = row.GetProperty("asset").GetString() ?? "";
                var symbol = row.GetProperty("symbol").GetString() ?? "";
                bool perpetual = row.GetProperty("perpetual").GetBoolean();
                _ = MarketCoin.ChartUrl(symbol, perpetual);
                if (!Regex.IsMatch(asset, @"^[\p{L}\p{N}]{1,40}$")) throw new InvalidDataException("Invalid market asset.");
                double? change = row.TryGetProperty("change24h", out var percent) && percent.ValueKind == JsonValueKind.Number && percent.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
                long? quoteAt = row.TryGetProperty("quoteAt", out var time) && time.ValueKind == JsonValueKind.Number && time.TryGetInt64(out var milliseconds) ? milliseconds : null;
                rows.Add(new(asset, symbol, perpetual, change, quoteAt));
            }
            if (rows.Select(row => (row.Symbol, row.Perpetual)).Distinct().Count() != rows.Count) throw new InvalidDataException("Duplicate market symbols.");
            return rows;
        }
        long? at = root.TryGetProperty("snapshotAt", out var timestamp) && timestamp.ValueKind == JsonValueKind.Number && timestamp.TryGetInt64(out var ms) ? ms : null;
        long? serverTime = root.TryGetProperty("at", out var generated) && generated.ValueKind == JsonValueKind.Number && generated.TryGetInt64(out var serverMs) ? serverMs : null;
        return new(at, Read("hot"), Read("gainers"), Read("losers"), Message("error"), Message("hotError")) { ServerTime = serverTime };
    }
}
public sealed record MarketFeedConfiguration(string Endpoint, string Token, bool AllowPrivateHttp = false)
{
    public static bool ValidEndpoint(string endpoint, bool allowPrivateHttp = false)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;
        if (uri.Scheme == Uri.UriSchemeHttps) return true;
        if (!allowPrivateHttp || uri.Scheme != Uri.UriSchemeHttp || !System.Net.IPAddress.TryParse(uri.Host, out var address)) return false;
        var bytes = address.GetAddressBytes();
        return System.Net.IPAddress.IsLoopback(address) || bytes.Length == 4 && (bytes[0] == 10 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31);
    }
    public static MarketFeedConfiguration Load(string directory)
    {
        var file = Path.Combine(directory, "market-feed.json");
        if (!File.Exists(file)) throw new InvalidDataException(Loc.T("请配置 market-feed.json 中的行情服务", "Configure the market service in market-feed.json"));
        var configuration = JsonSerializer.Deserialize<MarketFeedConfiguration>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (configuration is null || !ValidEndpoint(configuration.Endpoint, configuration.AllowPrivateHttp) || string.IsNullOrWhiteSpace(configuration.Token))
            throw new InvalidDataException(Loc.T("行情服务地址或只读授权未配置", "Market service URL or read-only credential is missing"));
        return configuration;
    }
}
public sealed class MarketFeedClient(HttpClient http, MarketFeedConfiguration configuration)
{
    public async Task<MarketFeedSnapshot> ReadAsync(CancellationToken cancellation)
    {
        if (!MarketFeedConfiguration.ValidEndpoint(configuration.Endpoint, configuration.AllowPrivateHttp)) throw new InvalidDataException("HTTPS or an explicitly allowed private LAN address is required.");
        using var request = new HttpRequestMessage(HttpMethod.Get, configuration.Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration.Token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(response.StatusCode == System.Net.HttpStatusCode.Unauthorized
            ? Loc.T("行情只读授权已失效", "Market read-only credential has expired") : $"Market feed HTTP {(int)response.StatusCode}");
        if (response.Content.Headers.ContentLength > 1_000_000) throw new InvalidDataException("Market response too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(chunk, cancellation).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > 1_000_000) throw new InvalidDataException("Market response too large.");
            buffer.Write(chunk, 0, read);
        }
        var snapshot = MarketFeedSnapshot.Parse(System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
        // Ranking snapshots may be hours old; only the response generation time can calibrate a clock.
        return snapshot with { ServerTime = response.Headers.Age is { } age && age > TimeSpan.Zero ? null : snapshot.ServerTime ?? response.Headers.Date?.ToUnixTimeMilliseconds() };
    }
}
