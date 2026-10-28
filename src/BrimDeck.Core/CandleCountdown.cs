using System.Globalization;

namespace BrimDeck.Core;

[Flags]
public enum CandlePeriods { None = 0, FifteenMinutes = 1, OneHour = 2, FourHours = 4, All = 7 }

public static class CandleCountdown
{
    private static readonly CandlePeriods[] Periods = [CandlePeriods.FifteenMinutes, CandlePeriods.OneHour, CandlePeriods.FourHours];
    public static IReadOnlyList<CandlePeriods> Selected(CandlePeriods selected) => Periods.Where(period => (selected & period) != 0).ToArray();
    public static CandlePeriods ChartPeriod(IEnumerable<CandlePeriods> closed) => closed.Contains(CandlePeriods.FourHours)
        ? CandlePeriods.FourHours : closed.Contains(CandlePeriods.OneHour) ? CandlePeriods.OneHour : CandlePeriods.None;
    public static string Label(CandlePeriods period) => period switch
    { CandlePeriods.FifteenMinutes => "15m", CandlePeriods.OneHour => "1H", CandlePeriods.FourHours => "4H", _ => throw new ArgumentOutOfRangeException(nameof(period)) };
    public static TimeSpan Duration(CandlePeriods period) => period switch
    { CandlePeriods.FifteenMinutes => TimeSpan.FromMinutes(15), CandlePeriods.OneHour => TimeSpan.FromHours(1), CandlePeriods.FourHours => TimeSpan.FromHours(4), _ => throw new ArgumentOutOfRangeException(nameof(period)) };
    public static long OpenMilliseconds(DateTimeOffset now, CandlePeriods period)
    {
        long duration = (long)Duration(period).TotalMilliseconds, timestamp = now.ToUnixTimeMilliseconds();
        return timestamp - ((timestamp % duration + duration) % duration);
    }
    public static TimeSpan Remaining(DateTimeOffset now, CandlePeriods period)
        => TimeSpan.FromMilliseconds(OpenMilliseconds(now, period) + Duration(period).TotalMilliseconds - now.ToUnixTimeMilliseconds());
    public static string Format(DateTimeOffset now, CandlePeriods period)
    {
        int seconds = (int)Math.Ceiling(Remaining(now, period).TotalSeconds);
        return period == CandlePeriods.FourHours
            ? string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{seconds / 60:00}:{seconds % 60:00}");
    }
    public static int Urgency(TimeSpan remaining) => remaining.TotalSeconds <= 10 ? 2 : remaining.TotalSeconds <= 60 ? 1 : 0;
}

// One gate per application, shared by every display. A late/resumed tick establishes a baseline without replaying closes.
public sealed class CandleCloseGate
{
    private DateTimeOffset? _previous;
    private readonly Dictionary<CandlePeriods, long> _observed = [];
    public void Reset() { _previous = null; _observed.Clear(); }
    public IReadOnlyList<CandlePeriods> Observe(DateTimeOffset now, CandlePeriods selected)
    {
        var closed = new List<CandlePeriods>();
        bool timely = _previous is { } previous && now >= previous && now - previous <= TimeSpan.FromSeconds(3);
        foreach (var period in CandleCountdown.Selected(selected))
        {
            long open = CandleCountdown.OpenMilliseconds(now, period);
            if (_observed.TryGetValue(period, out long last) && open > last && timely && now.ToUnixTimeMilliseconds() - open <= 3000) closed.Add(period);
            _observed[period] = Math.Max(open, _observed.GetValueOrDefault(period, long.MinValue));
        }
        foreach (var period in _observed.Keys.Where(period => (selected & period) == 0).ToArray()) _observed.Remove(period);
        _previous = now;
        return closed;
    }
}

// A server sample anchors a monotonic clock: changing the Windows clock cannot move a running candle.
public sealed class CandleClock(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private DateTimeOffset? _server;
    private long _received;
    private bool _available;
    public bool IsSynchronized => _available && _server is not null && _time.GetElapsedTime(_received) <= TimeSpan.FromMinutes(5);
    public DateTimeOffset UtcNow => _server is { } server ? server + _time.GetElapsedTime(_received) : _time.GetUtcNow();
    public bool Synchronize(long serverMilliseconds, TimeSpan roundTrip)
    {
        if (roundTrip < TimeSpan.Zero || roundTrip > TimeSpan.FromSeconds(5)) return false;
        try { _server = DateTimeOffset.FromUnixTimeMilliseconds(serverMilliseconds) + roundTrip / 2; }
        catch (ArgumentOutOfRangeException) { return false; }
        _received = _time.GetTimestamp(); _available = true; return true;
    }
    public void MarkUnavailable() => _available = false;
}
