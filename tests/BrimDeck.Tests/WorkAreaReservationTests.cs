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
    }
}
