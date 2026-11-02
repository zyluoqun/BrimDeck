namespace BrimDeck.Core;

public static class ChartIntervals
{
    public const string Default = "4h";
    public static IReadOnlyList<string> All { get; } = ["1s", "1m", "5m", "15m", "30m", "1h", "4h", "1d", "1w", "1M", "3M", "6M", "1Y"];
    public static string Normalize(string? interval) => interval is not null && All.Contains(interval, StringComparer.Ordinal) ? interval : Default;
    public static string Label(string interval) => interval switch {
        "1s" => Loc.T("1 秒", "1 second"), "1m" => Loc.T("1 分钟", "1 minute"), "5m" => Loc.T("5 分钟", "5 minutes"),
        "15m" => Loc.T("15 分钟", "15 minutes"), "30m" => Loc.T("30 分钟", "30 minutes"), "1h" => Loc.T("1 小时", "1 hour"),
        "1d" => Loc.T("1 天", "1 day"), "1w" => Loc.T("1 周", "1 week"), "1M" => Loc.T("1 个月", "1 month"),
        "3M" => Loc.T("3 个月", "3 months"), "6M" => Loc.T("6 个月", "6 months"), "1Y" => Loc.T("1 年", "1 year"),
        _ => Loc.T("4 小时", "4 hours") };
}
