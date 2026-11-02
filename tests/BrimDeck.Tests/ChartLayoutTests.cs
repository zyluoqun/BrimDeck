using System.Text.Json;
using BrimDeck.Core;

internal static class ChartLayoutTests
{
    internal static JsonElement State(int count, string mode = "custom", bool sync = false) => JsonSerializer.SerializeToElement(new {
        workspaceId = "layout123", workspace = new { multiCharts = new {
            mode, layout = new { cols = count, rows = 1, cells = Enumerable.Range(0, count).Select(i => new { col = i, row = 0, colSpan = 1, rowSpan = 1 }).ToArray() },
            syncStatus = new { symbol = sync, interval = false } } },
        panes = Enumerable.Range(0, count).Select(i => new { chartIndex = i, symbol = "COIN" + i, interval = "1h" }).ToArray() });

    public static void Run(Action<string, bool> check)
    {
        foreach (int count in new[] { 1, 2, 3, 4, 5, 9, 12 })
        {
            var layout = ChartLayout.Parse(State(count), "layout123");
            check($"A {count}-chart workspace uses its visible cells, without a four-pane limit", layout.Panes.Count == count
                && layout.Panes.Select(p => p.Index).SequenceEqual(Enumerable.Range(0, count)) && layout.Panes[0].Symbol == "COIN0");
        }
        var original = ChartLayout.Parse(State(2), "layout123");
        var changed = ChartLayout.Parse(State(3), "layout123");
        check("Changing the visible pane structure changes the approval signature", original.Signature != changed.Signature);
        check("Changing sync settings does not change pane identity", original.Signature == ChartLayout.Parse(State(2, sync: true), "layout123").Signature);
        foreach (string mode in new[] { "1", "2H", "2V", "3H", "3V", "4" })
        {
            var state = JsonSerializer.SerializeToElement(new { workspaceId = "layout123", workspace = new { multiCharts = new { mode } } });
            var layout = ChartLayout.Parse(state, "layout123");
            int expected = mode == "4" ? 4 : mode[0] - '0';
            check($"Standard {mode} can be read without an explicit grid", layout.Panes.Count == expected);
        }
        bool Refused(string json, string id = "layout123")
        {
            try { ChartLayout.Parse(JsonSerializer.Deserialize<JsonElement>(json), id); return false; }
            catch (InvalidOperationException) { return true; }
        }
        check("A response for another workspace is refused", Refused(State(2).GetRawText(), "other"));
        check("Unknown implicit layouts are refused", Refused("{\"workspaceId\":\"layout123\",\"workspace\":{\"multiCharts\":{\"mode\":\"custom\"}}}"));
        check("Overlapping cells are refused", Refused(State(2).GetRawText().Replace("\"col\":1", "\"col\":0")));
        check("Monitor grids are not mistaken for chart panes", Refused(State(2).GetRawText().Replace("\"cols\":2", "\"monitorGrid\":true,\"cols\":2")));
        const string raw = "{\"workspaceId\":\"layout123\",\"workspace\":{\"_id\":\"main\",\"metadata\":{\"symbol\":\"BTCUSDT\",\"interval\":\"4h\"},\"multiCharts\":{\"mode\":\"2H\",\"workspaces\":[{\"_id\":\"child\",\"metadata\":{\"symbol\":\"ETHUSDT\",\"interval\":\"1h\"}},{\"_id\":\"historical\"}]}}}";
        var snapshot = ChartLayout.Parse(JsonSerializer.Deserialize<JsonElement>(raw), "layout123");
        check("Raw snapshots show main and child chart markets, ignoring hidden historical charts", snapshot.Panes.Count == 2
            && snapshot.Panes[0].Symbol == "BTCUSDT" && snapshot.Panes[1].Symbol == "ETHUSDT" && snapshot.Panes[1].Interval == "1h");
        check("Changing a visible chart identity requires reconfirmation", snapshot.Signature != ChartLayout.Parse(JsonSerializer.Deserialize<JsonElement>(raw.Replace("\"child\"", "\"replacement\"")), "layout123").Signature);
        check("Hidden historical chart changes do not invalidate the visible layout", snapshot.Signature == ChartLayout.Parse(JsonSerializer.Deserialize<JsonElement>(raw.Replace("historical", "different")), "layout123").Signature);
        var settings = new DeckSettings { MarketChartWorkspaces = new() { ["layout123"] = new() { LayoutSignature = original.Signature, PinnedPanes = [0, 1] } } };
        settings.Normalize();
        var roundTrip = JsonSerializer.Deserialize<DeckSettings>(JsonSerializer.Serialize(settings))!;
        check("All pinned panes and the approval survive saving and normalization", roundTrip.MarketChartWorkspaces["layout123"].PinnedPanes.SequenceEqual([0, 1])
            && roundTrip.MarketChartWorkspaces["layout123"].LayoutSignature == original.Signature);
        var copy = settings.Copy(); copy.MarketChartWorkspaces["layout123"].PinnedPanes.Clear();
        check("Workspace pin settings are copied independently", settings.MarketChartWorkspaces["layout123"].PinnedPanes.Count == 2);
        settings.MarketChartWorkspaces["other"] = new() { PinnedPanes = [4] };
        check("Different workspaces keep separate pin selections", settings.MarketChartWorkspaces["layout123"].PinnedPanes.SequenceEqual([0, 1]));
        check("Existing workspace settings default to four hours", roundTrip.MarketChartWorkspaces["layout123"].Interval == "4h");
        settings.MarketChartWorkspaces["layout123"].Interval = "15m";
        settings.MarketChartWorkspaces["other"].Interval = "1M";
        settings.Normalize();
        var intervals = JsonSerializer.Deserialize<DeckSettings>(JsonSerializer.Serialize(settings))!;
        check("Each workspace keeps its own interval after a restart", intervals.MarketChartWorkspaces["layout123"].Interval == "15m"
            && intervals.MarketChartWorkspaces["other"].Interval == "1M" && settings.Copy().MarketChartWorkspaces["layout123"].Interval == "15m");
        settings.MarketChartWorkspaces["other"].Interval = "unknown";
        settings.Normalize();
        check("An unsupported saved interval falls back without changing pins or layout approval", settings.MarketChartWorkspaces["other"].Interval == "4h"
            && settings.MarketChartWorkspaces["other"].PinnedPanes.SequenceEqual([4]) && settings.MarketChartWorkspaces["layout123"].LayoutSignature == original.Signature);
    }
}
