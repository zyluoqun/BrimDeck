using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using BrimDeck.Core;

namespace BrimDeck.Native;

internal static class ChromeChartPlacement
{
    internal readonly record struct WindowCandidate(IntPtr Handle, string Title);
    internal static DisplayInfo TargetDisplay() => Displays.Current().FirstOrDefault(d => d.Device.Equals(@"\\.\DISPLAY2", StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException(Loc.T("未找到 2 号显示器，请检查 Windows 显示设置后重试", "Display 2 is not connected; check Windows display settings"));
    internal static async Task MaximizeAsync(DisplayInfo display, string tabTitle, CancellationToken cancellation, Action<string>? diagnostic = null)
    {
        var window = await WaitForWindowAsync(tabTitle, ReadWindows, Activate, cancellation, diagnostic).ConfigureAwait(false);
        if (MonitorFromWindow(window, 2) == display.Handle && IsZoomed(window)) return;
        ShowWindow(window, 9);
        var bounds = display.Bounds;
        if (!SetWindowPos(window, IntPtr.Zero, (int)bounds.Left + 40, (int)bounds.Top + 40,
            Math.Max(320, (int)bounds.Width - 80), Math.Max(240, (int)bounds.Height - 80), 0x0004))
            throw new InvalidOperationException(Loc.T("无法将四屏窗口移到 2 号屏，请重试", "Could not move the chart window to display 2"));
        ShowWindow(window, 3);
        if (MonitorFromWindow(window, 2) != display.Handle || !IsZoomed(window))
            throw new InvalidOperationException(Loc.T("四屏窗口尚未在 2 号屏最大化，请重试", "The chart window is not maximized on display 2; retry"));
    }
    internal static async Task<bool> IsOnDisplayAsync(DisplayInfo display, string tabTitle, CancellationToken cancellation, Action<string>? diagnostic = null)
    {
        var window = await WaitForWindowAsync(tabTitle, ReadWindows, Activate, cancellation, diagnostic).ConfigureAwait(false);
        return MonitorFromWindow(window, 2) == display.Handle;
    }
    internal static async Task<IntPtr> WaitForWindowAsync(string tabTitle,
        Func<(IntPtr Foreground, IReadOnlyList<WindowCandidate> Windows)> readWindows, Action<IntPtr> activate, CancellationToken cancellation, Action<string>? diagnostic = null)
    {
        // The extension binds this click to a unique temporary window marker. Coin titles and prices
        // cannot identify a workspace: two independent chart windows can show the same market.
        string stableTitle = tabTitle;
        for (int attempt = 0; attempt < 30; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            var state = readWindows();
            var matches = state.Windows.Where(w => stableTitle.Length > 0 && w.Title.Contains(stableTitle, StringComparison.Ordinal)).ToArray();
            if (matches.Any(w => w.Handle == state.Foreground))
            {
                diagnostic?.Invoke($"Chrome focus confirmed attempt={attempt + 1}");
                return state.Foreground;
            }
            if (attempt % 5 == 0) diagnostic?.Invoke($"Chrome focus waiting attempt={attempt + 1} matchingWindows={matches.Length}");
            // An ambiguous title must not move or activate an unrelated Chrome window.
            if (matches.Length == 1 && attempt % 3 == 0) activate(matches[0].Handle);
            if (attempt < 29) await Task.Delay(100, cancellation).ConfigureAwait(false);
        }
        throw new InvalidOperationException(Loc.T("四屏 Chrome 窗口未获得焦点，请重试", "The chart Chrome window did not receive focus; retry"));
    }
    private static (IntPtr Foreground, IReadOnlyList<WindowCandidate> Windows) ReadWindows()
    {
        var windows = new List<WindowCandidate>();
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window)) return true;
            try
            {
                GetWindowThreadProcessId(window, out var pid);
                using var process = Process.GetProcessById((int)pid);
                if (process.ProcessName != "chrome") return true;
                var title = new StringBuilder(1024); GetWindowText(window, title, title.Capacity);
                if (title.Length > 0) windows.Add(new(window, title.ToString()));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return true;
        }, IntPtr.Zero);
        return (GetForegroundWindow(), windows);
    }
    private static void Activate(IntPtr window)
    {
        if (IsIconic(window)) ShowWindowAsync(window, 9);
        SetForegroundWindow(window);
    }
    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32")] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32")] private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int length);
    [DllImport("user32")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32")] private static extern bool IsZoomed(IntPtr window);
}
