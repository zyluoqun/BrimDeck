using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BrimDeck.Core;
using Microsoft.Win32;

namespace BrimDeck.Native;

internal sealed class ChromeChartBridge : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sync = new();
    private readonly string _pipeName;
    private readonly Action<string>? _diagnostic;
    private TaskCompletionSource<NamedPipeServerStream> _connected = NewConnection();
    private NamedPipeServerStream? _pipe;
    private TaskCompletionSource<JsonElement>? _reply;
    private string? _requestId;
    private static TaskCompletionSource<NamedPipeServerStream> NewConnection() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static string PipeName => "BrimDeck.Chrome." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserDomainName + "\\" + Environment.UserName)))[..24];
    public ChromeChartBridge(string? pipeName = null, Action<string>? diagnostic = null) { _pipeName = pipeName ?? PipeName; _diagnostic = diagnostic; _ = ListenAsync(); }
    internal static string WindowIdentity(JsonElement result)
    {
        if (result.TryGetProperty("protocolVersion", out var protocol) && protocol.ValueKind == JsonValueKind.Number && protocol.TryGetInt32(out int version) && version >= 4
            && result.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && Guid.TryParseExact(id.GetString(), "N", out _)
            && result.TryGetProperty("nativeMarker", out var marker) && marker.ValueKind == JsonValueKind.String
            && marker.GetString() == " [BrimDeck-" + id.GetString() + "]") return marker.GetString()!;
        throw new InvalidOperationException(Loc.T("Chrome 扩展已更新，请在 chrome://extensions 中重新加载 BrimDeck Chart Window 后重试", "Reload the updated BrimDeck Chart Window extension at chrome://extensions, then retry"));
    }

    private async Task ListenAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(_lifetime.Token).ConfigureAwait(false);
                lock (_sync) { _pipe = pipe; _connected.TrySetResult(pipe); }
                _diagnostic?.Invoke("Chrome native host connected");
                while (await ChromeChartProtocol.ReadAsync(pipe, _lifetime.Token).ConfigureAwait(false) is { } message)
                {
                    lock (_sync)
                        if (message.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == _requestId) _reply?.TrySetResult(message);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or ObjectDisposedException) { }
            finally
            {
                lock (_sync)
                {
                    _pipe = null; _connected = NewConnection();
                    _reply?.TrySetException(new IOException(Loc.T("Chrome 联动连接已断开，请重试", "Chrome connection closed; retry the chart switch")));
                }
                _diagnostic?.Invoke("Chrome native host disconnected");
            }
        }
    }

    public async Task EnsureAsync(string workspaceId, CancellationToken cancellation)
    {
        workspaceId = OpenMarketChartClient.ParseWorkspace(workspaceId);
        var display = ChromeChartPlacement.TargetDisplay();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        long expiresAt = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeMilliseconds();
        Task<NamedPipeServerStream> connection;
        lock (_sync) connection = _connected.Task;
        if (!connection.IsCompleted)
        {
            bool running = false;
            foreach (var process in Process.GetProcessesByName("chrome")) { running = true; process.Dispose(); }
            if (!running)
            {
                var start = new ProcessStartInfo(FindChrome()) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
                start.ArgumentList.Add("--new-window"); start.ArgumentList.Add("https://openmarket.xyz/chart/" + workspaceId);
                Process.Start(start)?.Dispose();
            }
        }
        string? windowIdentity = null;
        int windowId = -1, tabId = -1;
        try
        {
            var result = await RequestWindowAsync(workspaceId, deadline.Token, expiresAt, targetBounds: display.Bounds).ConfigureAwait(false);
            if (!result.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                throw new InvalidOperationException(result.TryGetProperty("error", out var error) ? error.GetString() : "Chrome chart window failed.");
            windowId = result.GetProperty("windowId").GetInt32(); tabId = result.GetProperty("tabId").GetInt32();
            windowIdentity = WindowIdentity(result);
            bool wasFullscreen = result.TryGetProperty("state", out var state) && state.GetString() == "fullscreen";
            bool onTarget = await ChromeChartPlacement.IsOnDisplayAsync(display, windowIdentity, deadline.Token, _diagnostic).ConfigureAwait(false);
            // Monitor coordinates may differ across DPI settings. Never move a shared source window as a fallback.
            if (!onTarget && !(result.TryGetProperty("singleTab", out var single) && single.ValueKind == JsonValueKind.True))
                throw new InvalidOperationException(Loc.T("图表标签尚未移到 2 号屏，已保留原窗口位置，请检查显示器缩放或重试。", "The chart tab is not on display 2. The source window was left in place; check display scaling and retry."));
            bool staysFullscreen = wasFullscreen && onTarget;
            async Task SetState(string requested)
            {
                var changed = await RequestWindowAsync(workspaceId, deadline.Token, expiresAt, "window_state", windowId, tabId, requested).ConfigureAwait(false);
                if (!changed.TryGetProperty("ok", out var accepted) || accepted.ValueKind != JsonValueKind.True
                    || !changed.TryGetProperty("state", out var actual) || actual.GetString() != requested)
                    throw new InvalidOperationException(Loc.T("Chrome 尚未确认四屏全屏状态，请检查扩展后重试", "Chrome did not confirm the chart window state; check the extension and retry"));
            }
            if (wasFullscreen && !staysFullscreen) await SetState("normal").ConfigureAwait(false);
            if (!staysFullscreen) await ChromeChartPlacement.MaximizeAsync(display, windowIdentity, deadline.Token, _diagnostic).ConfigureAwait(false);
            await SetState("fullscreen").ConfigureAwait(false);
            _diagnostic?.Invoke("Chrome fullscreen confirmed on display 2");
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new InvalidOperationException(Loc.T("Chrome 四屏联动未就绪。请确认已在使用的 Chrome 用户中加载 BrimDeck Chart Window 扩展，再重试。", "Chrome chart connection is not ready. Load BrimDeck Chart Window in your Chrome profile, then retry.")); }
        finally
        {
            if (windowIdentity is not null)
            {
                using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                cleanup.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    var released = await RequestWindowAsync(workspaceId, cleanup.Token, DateTimeOffset.UtcNow.AddSeconds(2).ToUnixTimeMilliseconds(),
                        "release_marker", windowId, tabId, nativeMarker: windowIdentity).ConfigureAwait(false);
                    bool confirmed = released.TryGetProperty("ok", out var success) && success.ValueKind == JsonValueKind.True;
                    _diagnostic?.Invoke(confirmed ? "Chrome temporary window marker released" : "Chrome window marker cleanup deferred to automatic expiry");
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException or JsonException)
                { _diagnostic?.Invoke("Chrome window marker cleanup deferred to automatic expiry"); }
            }
        }
    }
    internal async Task<JsonElement> RequestWindowAsync(string workspaceId, CancellationToken cancellation, long? expiresAt = null,
        string type = "prepare", int? windowId = null, int? tabId = null, string? state = null, string? nativeMarker = null, System.Windows.Rect? targetBounds = null)
    {
        workspaceId = OpenMarketChartClient.ParseWorkspace(workspaceId);
        Task<NamedPipeServerStream> connection;
        lock (_sync) connection = _connected.Task;
        if (!connection.IsCompleted) _diagnostic?.Invoke($"Chrome request type={type} waiting for native host");
        var pipe = await connection.WaitAsync(cancellation).ConfigureAwait(false);
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        string id = Guid.NewGuid().ToString("N");
        lock (_sync) { _reply = response; _requestId = id; }
        try
        {
            var bounds = targetBounds.GetValueOrDefault();
            await ChromeChartProtocol.WriteAsync(pipe, new { type, id, workspaceId, windowId, tabId, state, nativeMarker,
                targetBounds = targetBounds.HasValue ? new { left = bounds.Left, top = bounds.Top, width = bounds.Width, height = bounds.Height } : null,
                expiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddSeconds(25).ToUnixTimeMilliseconds() }, cancellation).ConfigureAwait(false);
            _diagnostic?.Invoke($"Chrome request type={type} sent");
            var result = await response.Task.WaitAsync(cancellation).ConfigureAwait(false);
            _diagnostic?.Invoke($"Chrome reply type={type} received");
            return result;
        }
        finally { lock (_sync) { _reply = null; _requestId = null; } }
    }

    internal static string FindChrome()
        => FindChrome(InstalledChromeCandidates(), DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady)
            .Select(drive => drive.RootDirectory.FullName), File.Exists);

    internal static string FindChrome(IEnumerable<string> installedCandidates, IEnumerable<string> driveRoots, Func<string, bool> fileExists)
    {
        // Portable copies have no App Paths registration. Probe only known locations, without a recursive disk scan.
        var portableCandidates = driveRoots.SelectMany(root => new[]
        {
            Path.Combine(root, "Program Files", "Chrome", "chrome.exe"),
            Path.Combine(root, "Program Files (x86)", "Chrome", "chrome.exe"),
            Path.Combine(root, "Chrome", "chrome.exe")
        });
        foreach (string path in installedCandidates.Concat(portableCandidates).Distinct(StringComparer.OrdinalIgnoreCase))
            if (fileExists(path)) return path;
        throw new InvalidOperationException(Loc.T("未找到 Google Chrome，请先安装 Chrome", "Google Chrome was not found"));
    }

    private static IEnumerable<string> InstalledChromeCandidates()
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe");
            if (key?.GetValue("") is string path) yield return path;
        }
        foreach (var folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
        {
            string path = Path.Combine(folder, "Google", "Chrome", "Application", "chrome.exe");
            yield return path;
        }
    }

    internal static void RegisterHost(string directory)
    {
        string manifest = Path.Combine(directory, "chrome-chart-host.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new { name = ChromeChartProtocol.HostName, description = "BrimDeck chart window connection",
            path = Path.Combine(AppContext.BaseDirectory, "BrimDeck.exe"), type = "stdio", allowed_origins = new[] { "chrome-extension://" + ChromeChartProtocol.ExtensionId + "/" } }));
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Google\Chrome\NativeMessagingHosts\" + ChromeChartProtocol.HostName);
        key.SetValue("", manifest);
    }

    internal static async Task RunHostAsync(string origin, string? pipeName = null)
    {
        if (origin != "chrome-extension://" + ChromeChartProtocol.ExtensionId + "/") return;
        using var lifetime = new CancellationTokenSource();
        using var pipe = new NamedPipeClientStream(".", pipeName ?? PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(30000, lifetime.Token).ConfigureAwait(false);
            using var input = Console.OpenStandardInput(); using var output = Console.OpenStandardOutput();
            async Task Copy(Stream from, Stream to)
            {
                while (await ChromeChartProtocol.ReadAsync(from, lifetime.Token).ConfigureAwait(false) is { } message)
                    await ChromeChartProtocol.WriteAsync(to, message, lifetime.Token).ConfigureAwait(false);
            }
            var incoming = Copy(input, pipe); var outgoing = Copy(pipe, output);
            try
            {
                var completed = await Task.WhenAny(incoming, outgoing).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
            }
            finally
            {
                lifetime.Cancel(); pipe.Dispose(); input.Dispose(); output.Dispose();
                // Windows stdin reads can ignore cancellation. Waiting for Chrome's open input here strands the host
                // after BrimDeck restarts, so Chrome never sees a disconnect and never opens a fresh connection.
                _ = Task.WhenAll(incoming, outgoing).ContinueWith(completed => { _ = completed.Exception; },
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException or JsonException) { }
    }
    public void Dispose()
    {
        _lifetime.Cancel();
        lock (_sync) _pipe?.Dispose();
    }
}
