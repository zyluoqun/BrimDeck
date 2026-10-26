using System.Text.Json;
using BrimDeck.Core;

internal static class MarketCompositionTests
{
    public static void Run(Action<string, bool> check)
    {
        var settings = new DeckSettings { CapsuleSummary = true, CapsuleMusic = true, CapsuleMonitor = true, CapsuleClock = true };
        var before = (settings.CapsuleSummary, settings.CapsuleMusic, settings.CapsuleMonitor, settings.CapsuleClock,
            settings.OpenDelay, settings.CloseDelay, settings.Maximized, settings.Borderless, settings.Exclusive);
        settings.CapsuleMarket = true;
        settings.Normalize();
        check("Enabling market content preserves original switches, hover delays and window behavior", before ==
            (settings.CapsuleSummary, settings.CapsuleMusic, settings.CapsuleMonitor, settings.CapsuleClock,
                settings.OpenDelay, settings.CloseDelay, settings.Maximized, settings.Borderless, settings.Exclusive));
        check("Both text styles add market as an optional content part", settings.CapsuleOrder.Contains(CompactPart.Market)
            && settings.CapsuleOrder.Count == 4 && settings.NotchOrder.Contains(CompactPart.Market) && !settings.NotchMarket);
        settings.SetCompactOrder(CompactStyle.Capsule, [CompactPart.Music, CompactPart.Market, CompactPart.Rings, CompactPart.Monitor]);
        var saved = SettingsMigrations.Read(JsonSerializer.Serialize(settings));
        check("Market position, ranking and original enabled content survive settings reload", saved.CapsuleOrder.SequenceEqual(settings.CapsuleOrder)
            && saved.CapsuleMarket && saved.CapsuleMusic && saved.CapsuleSummary && saved.CapsuleMonitor && saved.CapsuleClock);
        var copied = settings.Copy(); copied.CapsuleMarket = false;
        check("Disabling market leaves the other capsule functions enabled", !copied.CapsuleMarket && copied.CapsuleSummary && copied.CapsuleMusic && copied.CapsuleMonitor && copied.CapsuleClock);

        var natural = CompactMusicLayout.NaturalWidth(1, true, 3, true, 40, 150, 280);
        var all = CompactMusicLayout.Calculate(natural, 1, true, 3, true, 40, 150, 280);
        check("All capsule content has space together at automatic width", all.RingWidth > 0 && all.TextWidth > 0 && all.MonitorWidth >= 150
            && all.CoverWidth > 0 && all.EqualizerWidth > 0 && all.ClockWidth >= 40 && all.MarketWidth == 280);
        var clockMarket = CompactMusicLayout.Calculate(400, 1, false, 0, false, 40, 0, 280);
        var monitorMarket = CompactMusicLayout.Calculate(500, 1, false, 0, false, 0, 150, 280);
        check("A clock and system monitor beside market content never overlap it as a standalone region", !clockMarket.ClockAlone && clockMarket.ClockWidth >= 40
            && !monitorMarket.MonitorAlone && monitorMarket.MonitorWidth >= 150);
        var only = CompactMusicLayout.Calculate(400, 1, false, 0, false, 0, 0, 280);
        check("Market alone can use the whole capsule width", only.MarketWidth == 400 && only.MarketGap == 0);
        foreach (double scale in new[] { .5, 1d, 2d })
        foreach (int width in new[] { 0, 1, 40, 100, 180, 320, 720, 1200 })
        {
            var layout = CompactMusicLayout.Calculate(width, scale, true, 3, true, 40 * scale, 150 * scale, 280 * scale);
            double used = layout.CoverWidth + layout.EqualizerWidth + layout.ClockWidth + layout.MonitorWidth
                + layout.RingWidth + layout.TextWidth + layout.Between + layout.MarketWidth + layout.MarketGap;
            check($"Coexisting content stays within {width} DIPs at scale {scale}", double.IsFinite(used) && used >= 0 && used <= width + .001);
        }
        var original = CompactMusicLayout.Calculate(600, 1, true, 3, true, 40, 150);
        check("Market disabled uses the unchanged original layout", original == CompactMusicLayout.Calculate(600, 1, true, 3, true, 40, 150, 0)
            && original.MarketWidth == 0 && original.MarketGap == 0);
    }
}
