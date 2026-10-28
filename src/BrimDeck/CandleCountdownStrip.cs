using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using BrimDeck.Core;

namespace BrimDeck;

internal sealed class CandleCountdownStrip : StackPanel
{
    private readonly List<(CandlePeriods Period, TextBlock Value)> _values = [];
    private readonly Ellipse _status;
    internal CandlePeriods Periods { get; }
    internal double ReservedWidth { get; }
    internal IReadOnlyList<string> Values => _values.Select(value => value.Value.Text).ToArray();
    internal CandleCountdownStrip(CandlePeriods periods, double scale = 1)
    {
        Periods = periods; Orientation = Orientation.Horizontal; VerticalAlignment = VerticalAlignment.Center;
        Height = CompactMusicLayout.Whole(24 * scale);
        _status = new Ellipse { Width = 4 * scale, Height = 4 * scale, Margin = new Thickness(0, 0, 6 * scale, 0), VerticalAlignment = VerticalAlignment.Center };
        Children.Add(_status);
        foreach (var period in CandleCountdown.Selected(periods))
        {
            var pair = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(_values.Count == 0 ? 0 : 10 * scale, 0, 0, 0) };
            pair.Children.Add(new TextBlock { Text = CandleCountdown.Label(period), FontSize = 10 * scale, Foreground = UI.Brush(UI.Secondary),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4 * scale, 0) });
            var value = new TextBlock { Text = period == CandlePeriods.FourHours ? "88:88:88" : "88:88", FontFamily = new FontFamily("Consolas"),
                FontSize = 12 * scale, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            value.Measure(new Size(double.PositiveInfinity, Height)); value.Width = Math.Ceiling(value.DesiredSize.Width);
            pair.Children.Add(value); Children.Add(pair); _values.Add((period, value));
        }
        Measure(new Size(double.PositiveInfinity, Height)); ReservedWidth = Math.Ceiling(DesiredSize.Width); Width = ReservedWidth;
        AutomationProperties.SetName(this, Loc.T("K 线收盘倒计时", "Candle close countdown"));
    }
    internal void Update(DateTimeOffset now, bool synchronized, bool highlight)
    {
        foreach (var (period, value) in _values)
        {
            value.Text = CandleCountdown.Format(now, period);
            int urgency = highlight ? CandleCountdown.Urgency(CandleCountdown.Remaining(now, period)) : 0;
            value.Foreground = UI.Brush(urgency == 2 ? "#FB7185" : urgency == 1 ? "#FF9F43" : UI.Primary);
            AutomationProperties.SetName(value, CandleCountdown.Label(period) + Loc.T(" 收盘剩余 ", " closes in ") + value.Text);
        }
        _status.Fill = UI.Brush(synchronized ? "#36D399" : "#FF9F43");
        ToolTip = synchronized ? Loc.T("币安 UTC K 线 · 已通过行情服务校时", "Binance UTC candles · synchronized with the market service")
            : Loc.T("校时暂不可用 · 使用本地估算，请检查行情连接", "Time synchronization unavailable · estimated time; check the market connection");
        AutomationProperties.SetHelpText(this, (string)ToolTip);
    }
}
