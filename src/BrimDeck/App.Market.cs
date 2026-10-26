using System.IO;
using System.Net.Http;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck;

public partial class App
{
    private readonly DispatcherTimer _marketTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly HttpClient _marketHttp = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(25) };
    private bool _marketBusy;
    internal MarketFeedSnapshot Market { get; private set; } = MarketFeedSnapshot.Empty;
    internal string? MarketError { get; private set; }
    internal event Action? MarketUpdated;
    private void StartMarketFeed()
    {
        _marketTimer.Tick += async (_, _) => await RefreshMarketAsync();
        _marketTimer.Start();
        _ = RefreshMarketAsync();
    }
    internal async Task RefreshMarketAsync()
    {
        if (_marketBusy || Exiting || !(Settings.MarketPage || Settings.CapsuleMarket || Settings.NotchMarket || Settings.LineMarket)) return;
        _marketTimer.Interval = TimeSpan.FromSeconds(Decks.Any(deck => deck.MarketDemand) ? 15 : 60);
        _marketBusy = true;
        try
        {
            var configuration = MarketFeedConfiguration.Load(AppContext.BaseDirectory);
            Market = await new MarketFeedClient(_marketHttp, configuration).ReadAsync(Lifetime.Token);
            MarketError = Market.Error;
        }
        catch (OperationCanceledException) { if (!Exiting) { Market = MarketFeedSnapshot.Empty; MarketError = Loc.T("行情连接超时", "Market connection timed out"); } }
        catch (Exception ex) when (ex is IOException or HttpRequestException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
        { Market = MarketFeedSnapshot.Empty; MarketError = ex is HttpRequestException ? Loc.T("行情服务暂不可用，请检查网络或只读授权", "Market service unavailable; check the network or read-only credential") : ex.Message; }
        finally { _marketBusy = false; if (!Exiting) MarketUpdated?.Invoke(); }
    }
}
