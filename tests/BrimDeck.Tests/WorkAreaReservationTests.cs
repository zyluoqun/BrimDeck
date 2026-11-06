using System.Text.Json;
using BrimDeck.Core;

internal static class WorkAreaReservationTests
{
    public static void Run(Action<string, bool> check)
    {
        var monitor = new DipRect(0, 0, 1920, 1080);
        var work = new DipRect(0, 0, 1920, 1032);
        var settings = new DeckSettings { Style = CompactStyle.Capsule };
        check("Maximized-window avoidance is off by default", !settings.ReserveCapsuleSpace
            && WorkAreaReservation.Height(settings, monitor, work, 1) == 0);
        settings.ReserveCapsuleSpace = true;
        check("The reservation includes the capsule's top offset and height",
            WorkAreaReservation.Height(settings, monitor, work, 1) == 37);
        settings.PanelScale = 150;
        check("Capsule avoidance respects panel scale and display DPI",
            WorkAreaReservation.Height(settings, monitor, work, 1.25) == 67);
        settings.PanelScale = 100;
        var belowTaskbar = new DipRect(0, 40, 1920, 992);
        check("A top taskbar is not counted twice",
            WorkAreaReservation.Height(settings, monitor, belowTaskbar, 1) == 37);
        settings.CapsuleTop = 200;
        settings.CapsuleHeight = 48;
        check("Moving and resizing the capsule updates the reserved height",
            WorkAreaReservation.Height(settings, monitor, work, 1) == 248);
        foreach (var style in new[] { CompactStyle.Notch, CompactStyle.Line })
        {
            settings.Style = style;
            check($"{style} does not reserve capsule space", WorkAreaReservation.Height(settings, monitor, work, 1) == 0);
        }
        settings.Style = CompactStyle.Capsule;
        settings.CapsuleTop = 4000;
        check("Even a lowered capsule leaves a usable work area",
            WorkAreaReservation.Height(settings, monitor, work, 1) < work.Height);
        var saved = SettingsMigrations.Read(JsonSerializer.Serialize(settings));
        check("The compatibility choice survives save and reload", saved.ReserveCapsuleSpace);
        check("Existing settings retain the previous workspace behavior",
            !SettingsMigrations.Read("{}").ReserveCapsuleSpace);

        foreach (var global in Enum.GetValues<WindowBehavior>())
        foreach (var own in new WindowBehavior?[] { null, WindowBehavior.Normal, WindowBehavior.Line, WindowBehavior.Hide })
        {
            var rules = new DeckSettings
            {
                Style = CompactStyle.Capsule, ReserveCapsuleSpace = true,
                Maximized = global, Borderless = WindowBehavior.Hide, Exclusive = WindowBehavior.Line,
                FullscreenRules = [new FullscreenRule("test-app") { Maximized = own, Borderless = WindowBehavior.Line, Exclusive = WindowBehavior.Hide }]
            };
            check($"Avoidance keeps the capsule for global {global} and app override {own}",
                rules.Behavior(ScreenContext.Maximized) == WindowBehavior.Normal
                && rules.Behavior(ScreenContext.Maximized, "test-app") == WindowBehavior.Normal
                && rules.Behavior(ScreenContext.BackgroundFullscreen, "test-app", backgroundKind: ScreenContext.Maximized) == WindowBehavior.Normal);
            check("Avoidance preserves global and app-specific full-screen behavior",
                rules.Behavior(ScreenContext.Borderless) == WindowBehavior.Hide && rules.Behavior(ScreenContext.Exclusive) == WindowBehavior.Line
                && rules.Behavior(ScreenContext.Borderless, "test-app") == WindowBehavior.Line
                && rules.Behavior(ScreenContext.Exclusive, "test-app") == WindowBehavior.Hide
                && rules.Behavior(ScreenContext.BackgroundFullscreen, "test-app", backgroundKind: ScreenContext.Borderless) == WindowBehavior.Line);
            check("Resolving avoidance never overwrites the saved choices", rules.Maximized == global && rules.FullscreenRules[0].Maximized == own);
            var restored = SettingsMigrations.Read(JsonSerializer.Serialize(rules));
            restored.ReserveCapsuleSpace = false;
            check("Turning avoidance off after a reload restores both maximized rules",
                restored.Behavior(ScreenContext.Maximized) == global && restored.Behavior(ScreenContext.Maximized, "test-app") == (own ?? global));
            foreach (var style in new[] { CompactStyle.Notch, CompactStyle.Line })
            {
                rules.Style = style;
                check($"Switching to {style} restores the original maximized choices",
                    rules.Behavior(ScreenContext.Maximized) == global && rules.Behavior(ScreenContext.Maximized, "test-app") == (own ?? global));
            }
            rules.Style = CompactStyle.Capsule;
            check("Returning to the capsule reapplies the temporary maximized override", rules.Behavior(ScreenContext.Maximized, "test-app") == WindowBehavior.Normal);
        }
    }
}
