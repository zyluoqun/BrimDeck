using BrimDeck.Core;

namespace BrimDeck;

public partial class App
{
    internal async Task<IReadOnlyList<ChartWorkspace>> ListMarketChartWorkspacesAsync()
    {
        await _chartOperationGate.WaitAsync(Lifetime.Token);
        try
        {
            var connection = ChartConnection;
            if (connection.Token.Length == 0) throw new InvalidOperationException(Loc.T("请先保存图表控制令牌。", "Save a chart-control token first."));
            var client = new OpenMarketChartClient(_chartHttp, connection.Token, "catalog", LogChartRequest);
            return await client.ListWorkspacesAsync(Lifetime.Token);
        }
        finally { _chartOperationGate.Release(); }
    }

    internal async Task<ChartLayout> SelectMarketChartWorkspaceAsync(ChartWorkspace workspace)
    {
        await _chartOperationGate.WaitAsync(Lifetime.Token);
        try
        {
            var connection = ChartConnection;
            if (connection.Token.Length == 0) throw new InvalidOperationException(Loc.T("请先保存图表控制令牌。", "Save a chart-control token first."));
            if (connection.WorkspaceId != workspace.Id) ConfigureMarketChart(workspace.Id, "");
            var client = new OpenMarketChartClient(_chartHttp, connection.Token, workspace.Id, LogChartRequest);
            return await client.ReadLayoutAsync(Lifetime.Token);
        }
        finally { _chartOperationGate.Release(); }
    }
}
