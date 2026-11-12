using System.Windows;
using BrimDeck.Core;
using BrimDeck.Native;

internal static class ChartWindowTests
{
    internal static async Task Run(Action<string, bool> check)
    {
        var first = new DisplayInfo("path-one", "First", @"\\.\DISPLAY1", new(1), true, new Rect(0, 0, 1920, 1080));
        var second = new DisplayInfo("path-two", "Second", @"\\.\DISPLAY2", new(2), false, new Rect(-2560, 0, 2560, 1440));
        var third = new DisplayInfo("path-three", "Third", @"\\.\DISPLAY3", new(3), false, new Rect(1920, -1080, 1920, 1080));
        DisplayInfo[] displays = [third, first, second];
        check("Legacy chart placement selects display 2 regardless of enumeration order", ChromeChartPlacement.TargetDisplay(null, displays) == second);
        check("Shared chart placement can select the primary monitor", ChromeChartPlacement.TargetDisplay(new(first.Id, first.Name), displays) == first);
        check("Shared chart placement can select a third monitor", ChromeChartPlacement.TargetDisplay(new(third.Id, third.Name), displays) == third);
        var renumbered = second with { Device = @"\\.\DISPLAY4", Primary = true };
        check("A saved chart monitor follows its stable identity when Windows renumbers it",
            ChromeChartPlacement.TargetDisplay(new(second.Id, second.Name), [first, renumbered]) == renumbered);
        bool disconnected = false;
        try { ChromeChartPlacement.TargetDisplay(new("missing", "Disconnected"), displays); }
        catch (InvalidOperationException ex) { disconnected = ex.Message.Contains("Disconnected"); }
        check("A disconnected chosen monitor reports its name instead of opening on another display", disconnected);
        bool legacyMissing = false;
        try { ChromeChartPlacement.TargetDisplay(null, [first]); } catch (InvalidOperationException) { legacyMissing = true; }
        check("Legacy display 2 being absent remains an explicit error", legacyMissing);

        foreach (bool fullscreen in new[] { false, true })
        foreach (bool wasFullscreen in new[] { false, true })
        foreach (bool onTarget in new[] { false, true })
        {
            var operations = new List<string>();
            await ChromeChartBridge.ApplyWindowStateAsync(wasFullscreen, onTarget, fullscreen,
                state => { operations.Add(state); return Task.CompletedTask; },
                () => { operations.Add("maximize"); return Task.CompletedTask; });
            string[] expected = (fullscreen, wasFullscreen, onTarget) switch
            {
                (true, true, true) => ["fullscreen"],
                (true, true, false) => ["normal", "maximize", "fullscreen"],
                (true, false, _) => ["maximize", "fullscreen"],
                (false, true, _) => ["normal", "maximize"],
                _ => ["maximize"]
            };
            check($"Chart opening fullscreen={fullscreen}, previousFullscreen={wasFullscreen}, onTarget={onTarget} applies the requested state",
                operations.SequenceEqual(expected));
        }
        bool failed = false, moved = false;
        try
        {
            await ChromeChartBridge.ApplyWindowStateAsync(true, true, false,
                _ => throw new InvalidOperationException("state refused"), () => { moved = true; return Task.CompletedTask; });
        }
        catch (InvalidOperationException) { failed = true; }
        check("A refused fullscreen exit stops before moving or maximizing the chart", failed && !moved);
    }
}
