using BrimDeck.Native;

internal static class ChromeFocusTests
{
    internal static async Task Run(Action<string, bool> check)
    {
        IntPtr chart = new(123), other = new(456), foreground = other;
        const string marker = " [BrimDeck-11111111111111111111111111111111]";
        int observations = 0, activations = 0;
        var windows = new[] { new ChromeChartPlacement.WindowCandidate(chart, "0.13120 ▲ ACUUSDT" + marker + " - Google Chrome"),
            new ChromeChartPlacement.WindowCandidate(other, "Other chart - Google Chrome") };
        var result = await ChromeChartPlacement.WaitForWindowAsync(marker, () =>
        {
            observations++;
            if (activations > 0 && observations >= 3) foreground = chart;
            return (foreground, windows);
        }, window => { if (window == chart) activations++; }, default);
        check("A minimized chart is activated and given time to become the foreground window", result == chart && observations >= 3 && activations > 0);

        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); bool stopped = false;
        try { await ChromeChartPlacement.WaitForWindowAsync(marker, () => (other, windows), _ => activations++, cancelled.Token); }
        catch (OperationCanceledException) { stopped = true; }
        check("Cancelled focus recovery cannot activate a window", stopped && activations == 1);

        int wrongActivations = 0;
        using var missing = new CancellationTokenSource(220); stopped = false;
        try { await ChromeChartPlacement.WaitForWindowAsync(" [BrimDeck-missing]", () => (other, windows), _ => wrongActivations++, missing.Token); }
        catch (OperationCanceledException) { stopped = true; }
        check("A different foreground chart is never used as the requested workspace", stopped && wrongActivations == 0);
        var duplicates = new[] { new ChromeChartPlacement.WindowCandidate(chart, "ACUUSDT" + marker + " - Google Chrome"), new ChromeChartPlacement.WindowCandidate(new(789), "ACUUSDT" + marker + " - Google Chrome") };
        using var ambiguous = new CancellationTokenSource(220); stopped = false;
        try { await ChromeChartPlacement.WaitForWindowAsync(marker, () => (other, duplicates), _ => wrongActivations++, ambiguous.Token); }
        catch (OperationCanceledException) { stopped = true; }
        check("Ambiguous Chrome titles cannot activate the wrong window", stopped && wrongActivations == 0);
        var sameMarket = new[] { new ChromeChartPlacement.WindowCandidate(chart, "2300.10 ETHUSDT" + marker + " - Google Chrome"),
            new ChromeChartPlacement.WindowCandidate(other, "2300.11 ETHUSDT - Google Chrome") };
        foreground = other;
        result = await ChromeChartPlacement.WaitForWindowAsync(marker, () => (foreground, sameMarket), window => foreground = window, default);
        check("Two live ETH chart windows are disambiguated by the prepared tab marker", result == chart && foreground == chart);
    }
}
