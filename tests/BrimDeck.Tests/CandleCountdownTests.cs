using System.Text.Json;
using BrimDeck.Core;

internal static class CandleCountdownTests
{
    public static void Run(Action<string, bool> check)
    {
        var boundary = new DateTimeOffset(2026, 10, 28, 16, 0, 0, TimeSpan.FromHours(8));
        check("Three candle periods are enabled by default without enabling sound or notifications",
            new DeckSettings() is { CandleCountdownPeriods: CandlePeriods.All, CandleCountdownSound: false, CandleCountdownNotification: false });
        check("Kline countdown uses UTC boundaries regardless of display timezone",
            CandleCountdown.Remaining(boundary.AddSeconds(-1), CandlePeriods.FourHours) == TimeSpan.FromSeconds(1)
            && CandleCountdown.Remaining(boundary.ToUniversalTime().AddSeconds(-1), CandlePeriods.FourHours) == TimeSpan.FromSeconds(1));
        check("A close immediately begins the next candle for all three periods", CandleCountdown.Selected(CandlePeriods.All)
            .All(period => CandleCountdown.Remaining(boundary, period) == CandleCountdown.Duration(period)));
        check("Subsecond countdowns round up instead of closing early",
            CandleCountdown.Format(boundary.AddMilliseconds(-1), CandlePeriods.FifteenMinutes) == "00:01");
        check("Short periods use mm:ss and four hours uses hh:mm:ss",
            CandleCountdown.Format(boundary, CandlePeriods.OneHour) == "60:00"
            && CandleCountdown.Format(boundary, CandlePeriods.FourHours) == "04:00:00");
        check("Countdown crosses midnight without negative values", CandleCountdown.Remaining(boundary.AddHours(16).AddMilliseconds(-10), CandlePeriods.FourHours) == TimeSpan.FromMilliseconds(10));
        check("Highlight thresholds include the last minute and last ten seconds",
            CandleCountdown.Urgency(TimeSpan.FromSeconds(61)) == 0 && CandleCountdown.Urgency(TimeSpan.FromSeconds(60)) == 1
            && CandleCountdown.Urgency(TimeSpan.FromSeconds(10)) == 2);

        var alerts = new CandleCloseGate();
        check("Starting a countdown never announces a historical close", alerts.Observe(boundary.AddMilliseconds(-500), CandlePeriods.All).Count == 0);
        check("Simultaneous closes are returned together", alerts.Observe(boundary.AddMilliseconds(100), CandlePeriods.All).SequenceEqual(CandleCountdown.Selected(CandlePeriods.All)));
        check("Repeated ticks cannot repeat a close alert", alerts.Observe(boundary.AddSeconds(1), CandlePeriods.All).Count == 0);
        check("Four-hour chart takes priority when all countdowns close together", CandleCountdown.ChartPeriod(CandleCountdown.Selected(CandlePeriods.All)) == CandlePeriods.FourHours);
        check("An ordinary hourly close opens the hourly chart", CandleCountdown.ChartPeriod([CandlePeriods.FifteenMinutes, CandlePeriods.OneHour]) == CandlePeriods.OneHour);
        check("A fifteen-minute close never opens a chart", CandleCountdown.ChartPeriod([CandlePeriods.FifteenMinutes]) == CandlePeriods.None);
        check("An empty close cannot open a chart", CandleCountdown.ChartPeriod([]) == CandlePeriods.None);
        alerts.Observe(boundary.AddSeconds(-1), CandlePeriods.All);
        check("A backward clock correction cannot repeat a close", alerts.Observe(boundary.AddSeconds(1), CandlePeriods.All).Count == 0);
        check("Resuming after sleep does not replay missed closes", alerts.Observe(boundary.AddMinutes(30), CandlePeriods.All).Count == 0);
        alerts.Observe(boundary.AddMinutes(45).AddMilliseconds(-500), CandlePeriods.All);
        check("New closes still announce after a long pause", alerts.Observe(boundary.AddMinutes(45), CandlePeriods.All).SequenceEqual([CandlePeriods.FifteenMinutes]));
        alerts.Reset(); alerts.Observe(boundary.AddSeconds(-1), CandlePeriods.FifteenMinutes);
        check("Enabling another period at a close does not announce it retroactively", alerts.Observe(boundary, CandlePeriods.All).SequenceEqual([CandlePeriods.FifteenMinutes]));

        var time = new CountdownTime(boundary);
        var clock = new CandleClock(time);
        check("An uncalibrated countdown explicitly uses local estimated time", !clock.IsSynchronized && clock.UtcNow == boundary);
        check("A fresh server sample calibrates the clock", clock.Synchronize(boundary.ToUnixTimeMilliseconds(), TimeSpan.FromMilliseconds(100)) && clock.IsSynchronized);
        time.Advance(TimeSpan.FromSeconds(10));
        check("Calibrated time advances with elapsed time", clock.UtcNow == boundary.AddMilliseconds(10050));
        time.Local = time.Local.AddHours(2);
        check("Changing the PC clock does not jump a calibrated countdown", clock.UtcNow == boundary.AddMilliseconds(10050));
        check("Slow server samples are rejected", !clock.Synchronize(boundary.ToUnixTimeMilliseconds(), TimeSpan.FromSeconds(8)));
        time.Advance(TimeSpan.FromMinutes(6));
        check("Old calibration is visibly stale", !clock.IsSynchronized);
        clock.Synchronize(boundary.ToUnixTimeMilliseconds(), TimeSpan.Zero); clock.MarkUnavailable();
        check("A network failure marks calibration as unavailable", !clock.IsSynchronized);
        check("Invalid server timestamps cannot break the clock", !clock.Synchronize(long.MaxValue, TimeSpan.Zero));

        var settings = new DeckSettings { CandleCountdownPeriods = (CandlePeriods)255 };
        settings.Normalize();
        check("Unknown period bits are discarded", settings.CandleCountdownPeriods == CandlePeriods.All);
        settings.CandleCountdownPeriods = CandlePeriods.OneHour;
        var saved = SettingsMigrations.Read(JsonSerializer.Serialize(settings)); saved.Normalize();
        check("Countdown choices survive save and reload", saved.CandleCountdownPeriods == CandlePeriods.OneHour
            && saved.ShowsCandleCountdown(CompactStyle.Notch) && saved.ShowsCandleCountdown(CompactStyle.Capsule)
            && !saved.ShowsCandleCountdown(CompactStyle.Line));
        settings.CandleCountdownPeriods = CandlePeriods.None;
        check("Deselecting every period stops countdown demand", !settings.CandleCountdownWanted);
        foreach (double scale in new[] { .5, 1d, 2d })
        foreach (double width in new[] { 0d, 1d, 40d, 180d, 320d, 640d, 1200d })
        {
            var layout = CompactMusicLayout.Calculate(width, scale, true, 3, true, 40 * scale, 150 * scale, 280 * scale, 260 * scale);
            double used = layout.CoverWidth + layout.EqualizerWidth + layout.ClockWidth + layout.MonitorWidth + layout.RingWidth
                + layout.TextWidth + layout.Between + layout.MarketWidth + layout.MarketGap + layout.CountdownWidth + layout.CountdownGap;
            check($"Countdown coexists within {width} DIPs at scale {scale}", double.IsFinite(used) && used <= width + .001 && used >= 0);
        }
        double natural = CompactMusicLayout.NaturalWidth(1, true, 3, true, 40, 150, 280, 260);
        var full = CompactMusicLayout.Calculate(natural, 1, true, 3, true, 40, 150, 280, 260);
        check("Automatic width displays all original modules and full countdowns", full.CountdownWidth == 260 && full.MarketWidth == 280
            && full.MonitorWidth >= 150 && full.TextWidth > 0 && full.ClockWidth >= 40 && full.RingWidth > 0);
    }

    private sealed class CountdownTime(DateTimeOffset initial) : TimeProvider
    {
        public DateTimeOffset Local = initial;
        private long _ticks;
        public override DateTimeOffset GetUtcNow() => Local;
        public override long GetTimestamp() => _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan elapsed) { _ticks += elapsed.Ticks; Local += elapsed; }
    }
}
