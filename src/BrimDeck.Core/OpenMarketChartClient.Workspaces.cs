using System.Text.Json;

namespace BrimDeck.Core;

public sealed partial class OpenMarketChartClient
{
    public async Task<IReadOnlyList<ChartWorkspace>> ListWorkspacesAsync(CancellationToken cancellation)
    {
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            await InitializeSessionAsync(cancellation).ConfigureAwait(false);
            var list = await CallAsync("chart_list", new { }, cancellation).ConfigureAwait(false);
            if (list.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException(Loc.T("无法读取图表方案列表，请稍后重试。", "Could not read the chart workspace list; retry shortly."));
            return list.EnumerateArray().Select(item => new ChartWorkspace(
                ParseWorkspace(Text(item, "shortId") is { Length: > 0 } id ? id : Text(item, "id")), Text(item, "name")))
                .DistinctBy(item => item.Id).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task<ChartLayout> ReadLayoutAsync(CancellationToken cancellation)
    {
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            await SelectWorkspaceAsync(cancellation).ConfigureAwait(false);
            var state = await CallAsync("chart_refresh", new { workspaceId = _workspace, full = true }, cancellation).ConfigureAwait(false);
            return ChartLayout.Parse(state, _workspace);
        }
        finally { _gate.Release(); }
    }
}
