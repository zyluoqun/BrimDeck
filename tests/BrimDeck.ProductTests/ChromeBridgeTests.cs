using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using BrimDeck.Core;
using BrimDeck.Native;

internal static class ChromeBridgeTests
{
    internal static async Task Run(Action<string, bool> check)
    {
        await CheckNativeHostShutdown(check);
        string pipeName = "BrimDeck.Tests.Chrome." + Guid.NewGuid().ToString("N");
        using var bridge = new ChromeChartBridge(pipeName);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(deadline.Token);
        var request = bridge.RequestWindowAsync("layout123", deadline.Token, targetBounds: new System.Windows.Rect(1920, 0, 1920, 1080));
        var command = (await ChromeChartProtocol.ReadAsync(client, deadline.Token))!.Value;
        check("Chrome bridge sends only a bounded workspace preparation request, without credentials", command.GetProperty("workspaceId").GetString() == "layout123"
            && command.GetProperty("type").GetString() == "prepare" && !command.TryGetProperty("token", out _)
            && command.GetProperty("expiresAt").GetInt64() > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        check("Chrome receives the target monitor bounds before choosing or transferring a tab", command.GetProperty("targetBounds").GetProperty("left").GetDouble() == 1920
            && command.GetProperty("targetBounds").GetProperty("width").GetDouble() == 1920);
        await ChromeChartProtocol.WriteAsync(client, new { id = "old-request", ok = true }, deadline.Token);
        await ChromeChartProtocol.WriteAsync(client, new { id = command.GetProperty("id").GetString(), ok = true, title = "ETHUSDT" }, deadline.Token);
        var reply = await request;
        check("Only the reply for this click can complete Chrome window preparation", reply.GetProperty("title").GetString() == "ETHUSDT");
        using var cancelled = new CancellationTokenSource();
        var pending = bridge.RequestWindowAsync("layout123", cancelled.Token);
        await ChromeChartProtocol.ReadAsync(client, deadline.Token); cancelled.Cancel();
        bool stopped = false;
        try { await pending; } catch (OperationCanceledException) { stopped = true; }
        check("Cancelling browser preparation stops waiting for its reply", stopped);
        var fullscreen = bridge.RequestWindowAsync("layout123", deadline.Token, type: "window_state", windowId: 3, tabId: 7, state: "fullscreen");
        var stateRequest = (await ChromeChartProtocol.ReadAsync(client, deadline.Token))!.Value;
        check("Fullscreen requests identify the exact prepared tab and window", stateRequest.GetProperty("type").GetString() == "window_state"
            && stateRequest.GetProperty("state").GetString() == "fullscreen" && stateRequest.GetProperty("windowId").GetInt32() == 3 && stateRequest.GetProperty("tabId").GetInt32() == 7);
        await ChromeChartProtocol.WriteAsync(client, new { id = stateRequest.GetProperty("id").GetString(), ok = true, state = "fullscreen" }, deadline.Token);
        check("Fullscreen preparation waits for the browser's state confirmation", (await fullscreen).GetProperty("state").GetString() == "fullscreen");
        string clickId = Guid.NewGuid().ToString("N"), marker = " [BrimDeck-" + clickId + "]";
        var identity = JsonSerializer.SerializeToElement(new { id = clickId, protocolVersion = 4, nativeMarker = marker });
        check("Native window identity is bound to this click instead of the coin title", ChromeChartBridge.WindowIdentity(identity) == marker);
        bool mismatchRejected = false;
        try { ChromeChartBridge.WindowIdentity(JsonSerializer.SerializeToElement(new { id = clickId, protocolVersion = 4, nativeMarker = " [BrimDeck-other]" })); }
        catch (InvalidOperationException) { mismatchRejected = true; }
        check("A foreign marker cannot identify this request's chart window", mismatchRejected);
        bool reloadRequested = false;
        try { ChromeChartBridge.WindowIdentity(JsonSerializer.SerializeToElement(new { id = clickId, protocolVersion = 3, nativeMarker = marker, title = "ETHUSDT" })); }
        catch (InvalidOperationException ex) { reloadRequested = ex.Message.Contains("重新加载") || ex.Message.Contains("Reload"); }
        check("An old extension is asked to reload instead of falling back to ambiguous coin titles", reloadRequested);
    }

    private static async Task CheckNativeHostShutdown(Action<string, bool> check)
    {
        foreach (bool closeChromeInput in new[] { true, false })
        {
            string pipeName = "BrimDeck.Tests.NativeHost." + Guid.NewGuid().ToString("N");
            using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(ChromeBridgeTests).Assembly.Location);
            start.ArgumentList.Add("--chrome-native-host-test"); start.ArgumentList.Add(pipeName);
            using var host = Process.Start(start)!;
            try
            {
                using var connectionDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await server.WaitForConnectionAsync(connectionDeadline.Token);
                await ChromeChartProtocol.WriteAsync(server, new { id = "probe" }, connectionDeadline.Token);
                var forwarded = await ChromeChartProtocol.ReadAsync(host.StandardOutput.BaseStream, connectionDeadline.Token);
                check("The real native host forwards framed replies while Chrome keeps standard input open",
                    forwarded?.GetProperty("id").GetString() == "probe");
                if (closeChromeInput) host.StandardInput.Close();
                else server.Dispose();
                bool exited = false;
                using var exitDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await host.WaitForExitAsync(exitDeadline.Token); exited = true; } catch (OperationCanceledException) { }
                check(closeChromeInput ? "Closing Chrome input releases the native host and its app connection"
                    : "Restarting BrimDeck releases the native host even while Chrome leaves standard input open",
                    exited && host.ExitCode == 0);
            }
            finally
            {
                if (!host.HasExited) { host.Kill(); await host.WaitForExitAsync(); }
            }
        }
    }
}
