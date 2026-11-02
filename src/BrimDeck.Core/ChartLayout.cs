using System.Text.Json;

namespace BrimDeck.Core;

public sealed record ChartWorkspace(string Id, string Name)
{
    public override string ToString() => Name + " · " + Id;
}
public sealed record ChartPane(int Index, int Column, int Row, int ColumnSpan, int RowSpan, string Symbol, string Interval);
public sealed record ChartLayout(string WorkspaceId, int Columns, int Rows, IReadOnlyList<ChartPane> Panes, string Signature, bool SymbolSync, bool IntervalSync)
{
    public bool IsLegacyFourPane => Columns == 3 && Rows == 2 && Panes.Count == 4
        && Panes[0] is { Column: 0, Row: 0, ColumnSpan: 3, RowSpan: 1 }
        && Panes.Skip(1).Select((pane, i) => pane.Column == i && pane.Row == 1 && pane.ColumnSpan == 1 && pane.RowSpan == 1).All(value => value);

    public static ChartLayout Parse(JsonElement state, string workspaceId)
    {
        static string Text(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        static int Number(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : -1;
        static bool Flag(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
        static InvalidOperationException BadLayout() => new(Loc.T("无法识别方案的图表布局，请刷新方案并重新确认。", "Could not read this workspace's chart layout; refresh and confirm it again."));
        if (Text(state, "workspaceId") != workspaceId || !state.TryGetProperty("workspace", out var workspace)
            || !workspace.TryGetProperty("multiCharts", out var multi) || multi.ValueKind != JsonValueKind.Object) throw BadLayout();
        int cols, rows;
        var panes = new List<ChartPane>();
        if (multi.TryGetProperty("layout", out var grid) && grid.ValueKind == JsonValueKind.Object)
        {
            if (Flag(grid, "monitorGrid") || Text(grid, "kind") is not ("" or "pane-grid")) throw BadLayout();
            cols = Number(grid, "cols"); rows = Number(grid, "rows");
            if (cols < 1 || rows < 1 || !grid.TryGetProperty("cells", out var cells) || cells.ValueKind != JsonValueKind.Array || cells.GetArrayLength() == 0) throw BadLayout();
            foreach (var cell in cells.EnumerateArray())
            {
                int col = Number(cell, "col"), row = Number(cell, "row"), colSpan = Number(cell, "colSpan"), rowSpan = Number(cell, "rowSpan");
                if (col < 0 || row < 0 || colSpan < 1 || rowSpan < 1 || col > cols - colSpan || row > rows - rowSpan
                    || panes.Any(p => col < p.Column + p.ColumnSpan && col + colSpan > p.Column && row < p.Row + p.RowSpan && row + rowSpan > p.Row)) throw BadLayout();
                panes.Add(new(panes.Count, col, row, colSpan, rowSpan, "", ""));
            }
        }
        else
        {
            (cols, rows) = Text(multi, "mode") switch { "1" => (1, 1), "2H" => (2, 1), "2V" => (1, 2), "3H" => (3, 1), "3V" => (1, 3), "4" => (2, 2), _ => throw BadLayout() };
            for (int i = 0; i < cols * rows; i++) panes.Add(new(i, i % cols, i / cols, 1, 1, "", ""));
        }
        // Only visible cells count. Saved workspaces can retain ids for charts no longer displayed.
        var snapshots = new List<JsonElement> { workspace };
        if (multi.TryGetProperty("workspaces", out var children) && children.ValueKind == JsonValueKind.Array)
            snapshots.AddRange(children.EnumerateArray().Take(panes.Count - 1));
        string[] ids = snapshots.Any(item => Text(item, "_id").Length > 0)
            ? snapshots.Select(item => Text(item, "_id")).ToArray()
            : multi.TryGetProperty("workspaceId", out var paneIds) && paneIds.ValueKind == JsonValueKind.Array
            ? paneIds.EnumerateArray().Take(panes.Count).Select(id => id.ValueKind == JsonValueKind.String ? id.GetString() ?? "" : "").ToArray() : [];
        string signature = JsonSerializer.Serialize(new { cols, rows, cells = panes.Select(p => new { p.Column, p.Row, p.ColumnSpan, p.RowSpan }), ids });
        for (int index = 0; index < Math.Min(panes.Count, snapshots.Count); index++)
            if (snapshots[index].TryGetProperty("metadata", out var metadata))
                panes[index] = panes[index] with { Symbol = Text(metadata, "symbol"), Interval = Text(metadata, "interval") };
        if (state.TryGetProperty("panes", out var markets) && markets.ValueKind == JsonValueKind.Array)
            foreach (var market in markets.EnumerateArray())
            {
                int index = Number(market, "chartIndex");
                if (index < 0) index = Number(market, "chart_index");
                if (index >= 0 && index < panes.Count) panes[index] = panes[index] with { Symbol = Text(market, "symbol"), Interval = Text(market, "interval") };
            }
        bool hasSync = multi.TryGetProperty("syncStatus", out var sync) && sync.ValueKind == JsonValueKind.Object;
        // Missing sync state must not permit writes that might affect pinned charts.
        bool KnownOff(string key) => hasSync && sync.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.False;
        return new(workspaceId, cols, rows, panes, signature, !KnownOff("symbol"), !KnownOff("interval"));
    }
}

public sealed class ChartRotationSettings
{
    public string Name { get; set; } = "";
    public string LayoutSignature { get; set; } = "";
    public string Interval { get; set; } = ChartIntervals.Default;
    public List<int> PinnedPanes { get; set; } = [];
    public bool AcceptsLayout(ChartLayout layout) => LayoutSignature.Length > 0
        && (LayoutSignature == layout.Signature || (PinnedPanes?.Count ?? 0) == 0);
    public ChartRotationSettings Copy() => new() { Name = Name, LayoutSignature = LayoutSignature, Interval = Interval, PinnedPanes = [.. PinnedPanes ?? []] };
}
