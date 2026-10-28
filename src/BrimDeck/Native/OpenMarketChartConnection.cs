using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BrimDeck.Core;

namespace BrimDeck.Native;

internal sealed record OpenMarketChartConnection(string WorkspaceId, string Token)
{
    private const string ConfigFile = "openmarket-chart.json", TokenFile = "openmarket-chart-token.dat";
    internal static OpenMarketChartConnection Load(string directory)
    {
        string config = Path.Combine(directory, ConfigFile), secret = Path.Combine(directory, TokenFile);
        if (!File.Exists(config) || !File.Exists(secret)) return new("", "");
        using var document = JsonDocument.Parse(File.ReadAllText(config));
        if (!document.RootElement.TryGetProperty("workspaceId", out var id) || id.ValueKind != JsonValueKind.String)
            throw new ArgumentException(Loc.T("图表联动配置缺少布局 ID，请重新保存配置", "Chart connection has no workspace ID; save the configuration again"));
        string workspace = OpenMarketChartClient.ParseWorkspace(id.GetString() ?? "");
        byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(secret), null, DataProtectionScope.CurrentUser);
        try { return new(workspace, Encoding.UTF8.GetString(plain)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    internal static void Save(string directory, string workspace, string token)
    {
        workspace = OpenMarketChartClient.ParseWorkspace(workspace);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, TokenFile);
        if (!string.IsNullOrWhiteSpace(token))
        {
            byte[] plain = Encoding.UTF8.GetBytes(token.Trim());
            try { File.WriteAllBytes(path + ".tmp", ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser)); File.Move(path + ".tmp", path, true); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        if (!File.Exists(path)) throw new InvalidOperationException(Loc.T("请先填写图表控制令牌", "Enter a chart-control token first"));
        string config = Path.Combine(directory, ConfigFile);
        File.WriteAllText(config + ".tmp", JsonSerializer.Serialize(new { workspaceId = workspace })); File.Move(config + ".tmp", config, true);
    }
}
