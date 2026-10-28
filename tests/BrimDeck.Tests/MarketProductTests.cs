using System.Text.Json;
using BrimDeck.Core;

internal static class MarketProductTests
{
    public static void Run(Action<string, bool> check)
    {
        var settings = new DeckSettings { UsagePage = true, MusicPage = true, MonitorPage = true, MarketPage = true };
        check("Four expanded modules retain the original page order", DeckPages.Visible(settings).SequenceEqual([DeckPage.Usage, DeckPage.Music, DeckPage.Monitor, DeckPage.Market]));
        check("Market page is independent of compact market switches", !settings.ShowsMarket(CompactStyle.Capsule) && DeckPages.Shown(settings, DeckPage.Market));
        settings.NotchMarket = settings.CapsuleMarket = settings.LineMarket = true;
        foreach (var style in Enum.GetValues<CompactStyle>()) check($"Market summary supports {style}", settings.ShowsMarket(style));
        check("Both text styles can sort market alongside original parts", settings.NotchOrder.Contains(CompactPart.Market) && settings.CapsuleOrder.Contains(CompactPart.Market));
        settings.DefaultPage = DefaultDeckPage.Music;
        check("Hover over market opens the full market page", DeckPages.OnHover(settings, DeckPage.Usage, true) == DeckPage.Market);
        check("Hover elsewhere retains the original opening preference", DeckPages.OnHover(settings, DeckPage.Usage, false) == DeckPage.Music);
        settings.MarketPage = false;
        check("A disabled market page never displaces original pages", DeckPages.OnHover(settings, DeckPage.Usage, true) == DeckPage.Music
            && DeckPages.Visible(settings).SequenceEqual([DeckPage.Usage, DeckPage.Music, DeckPage.Monitor]));
        settings.MusicPage = false;
        check("A disabled default page retains the last valid page", DeckPages.OnHover(settings, DeckPage.Monitor, false) == DeckPage.Monitor);
        settings.MusicPage = true;
        settings.MarketPage = true;
        var old = (settings.Width, settings.Height, settings.MusicWidth, settings.MusicHeight, settings.MonitorWidth, settings.MonitorHeight);
        DeckPages.SetSize(settings, DeckPage.Market, 700, 350);
        check("Market page resizing preserves the sizes of original modules", DeckPages.Size(settings, DeckPage.Market) == (700d, 350d)
            && old == (settings.Width, settings.Height, settings.MusicWidth, settings.MusicHeight, settings.MonitorWidth, settings.MonitorHeight));
        check("The expanded window reserves room for the market page", DeckPages.Largest(settings).Height >= 350 && DeckPages.Largest(settings).Width >= 700);
        settings.SetCompactOrder(CompactStyle.Notch, [CompactPart.Monitor, CompactPart.Market, CompactPart.Rings, CompactPart.Music]);
        var restored = SettingsMigrations.Read(JsonSerializer.Serialize(settings));
        check("Three styles and market page settings survive reload", restored.NotchMarket && restored.CapsuleMarket && restored.LineMarket && restored.MarketPage
            && restored.NotchOrder.SequenceEqual(settings.NotchOrder) && restored.MarketWidth == 700 && restored.MarketHeight == 350);
        settings.MarketWidth = double.NaN; settings.MarketHeight = double.PositiveInfinity; settings.Normalize();
        check("Market dimensions normalize without corrupting original settings", double.IsFinite(settings.MarketWidth) && double.IsFinite(settings.MarketHeight));
        check("Existing settings start with all three chart panes available", new DeckSettings().MarketChartPinnedPanes.Count == 0);
        settings.MarketChartPinnedPanes = [1, 3];
        restored = SettingsMigrations.Read(JsonSerializer.Serialize(settings));
        check("Pinned chart panes survive settings reload", restored.MarketChartPinnedPanes.SequenceEqual([1, 3]));
        var copy = settings.Copy(); copy.MarketChartPinnedPanes.Remove(1);
        check("Editing a settings copy cannot change the saved chart pins", settings.MarketChartPinnedPanes.SequenceEqual([1, 3]));
        settings.MarketChartPinnedPanes = [0, 3, 3, 4, 2, 1]; settings.Normalize();
        check("Invalid and duplicate pins normalize to at most two lower chart panes", settings.MarketChartPinnedPanes.SequenceEqual([3, 2]));
        restored = SettingsMigrations.Read("{\"MarketChartPinnedPanes\":null}");
        check("Null chart pins safely reload as an empty selection", restored.MarketChartPinnedPanes.Count == 0);
        settings.PageOrder = [DeckPage.Market, DeckPage.Monitor, DeckPage.Music, DeckPage.Usage];
        restored = SettingsMigrations.Read(JsonSerializer.Serialize(settings));
        check("Custom market participates in the upstream saved page order", DeckPages.Visible(restored).SequenceEqual(settings.PageOrder)
            && DeckPages.Resolve(restored, (DeckPage)999) == DeckPage.Market);
        restored = SettingsMigrations.Read("{\"PageOrder\":[2,1,0]}");
        check("Upstream settings without the market page retain it after the original pages", restored.PageOrder.SequenceEqual([DeckPage.Monitor, DeckPage.Music, DeckPage.Usage, DeckPage.Market]));
        foreach (double dpi in new[] { 1d, 1.25, 1.5, 2d })
        {
            double width = CompactMusicLayout.NaturalWidth(1, true, 3, true, 39.7, 150.2, 280, 260, dpi: dpi);
            check($"Automatic market and countdown width survives device pixel rounding at {dpi}", DevicePixels.Down(width, dpi) + 1e-6
                >= CompactMusicLayout.NaturalWidth(1, true, 3, true, 39.7, 150.2, 280, 260));
        }
    }
}
