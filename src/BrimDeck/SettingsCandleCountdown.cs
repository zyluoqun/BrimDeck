using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    private bool _candleCountdownOpen;
    private FrameworkElement CandleCountdownRow(CompactStyle style)
    {
        bool notch = style == CompactStyle.Notch;
        bool enabled = notch ? S.NotchCandleCountdown : S.CapsuleCandleCountdown;
        var content = new StackPanel();
        var periods = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var period in CandleCountdown.Selected(CandlePeriods.All))
        {
            var box = new CheckBox { Content = CandleCountdown.Label(period), IsChecked = (S.CandleCountdownPeriods & period) != 0,
                Foreground = UI.Brush(TextPrimary), FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 20, 0) };
            AutomationProperties.SetName(box, Loc.T("收盘倒计时 ", "Candle countdown ") + CandleCountdown.Label(period));
            void Select(bool on) => Change(s => s.CandleCountdownPeriods = on ? s.CandleCountdownPeriods | period : s.CandleCountdownPeriods & ~period);
            box.Checked += (_, _) => Select(true); box.Unchecked += (_, _) => Select(false); periods.Children.Add(box);
        }
        content.Children.Add(Row(Loc.T("显示周期", "Periods shown"), periods));
        content.Children.Add(Toggle(Loc.T("临近收盘变色（1 分钟 / 10 秒）", "Highlight before close (1 minute / 10 seconds)"), S.CandleCountdownHighlight, on => Change(s => s.CandleCountdownHighlight = on)));
        content.Children.Add(Toggle(Loc.T("收盘声音提醒", "Sound at candle close"), S.CandleCountdownSound, on => Change(s => s.CandleCountdownSound = on)));
        content.Children.Add(Toggle(Loc.T("收盘桌面通知", "Desktop notification at candle close"), S.CandleCountdownNotification, on => Change(s => s.CandleCountdownNotification = on)));
        content.Children.Add(Toggle(Loc.T("1H / 4H 收盘打开 BTC&ETH", "Open BTC&ETH at 1H / 4H close"), S.CandleCountdownChart, on => Change(s => s.CandleCountdownChart = on)));
        var chartHint = TextLine(Loc.T("自动切到 2 号屏的 BTC&ETH 左右双图：BTCUSDT / ETHUSDT 币安永续。1H、4H 同时收盘时优先 4H；仅在已校时时触发。请先配置市场行情的图表授权。", "Opens the BTC&ETH side-by-side Binance perpetual charts on display 2. 4H takes priority over a simultaneous 1H close; synchronized time and a configured chart connection are required."), 11, TextSecondary);
        chartHint.TextWrapping = TextWrapping.Wrap; chartHint.Margin = new Thickness(0, 0, 0, 8); content.Children.Add(chartHint);
        var hint = TextLine(Loc.T("三个周期固定并排显示，按币安 UTC K 线计算。绿点表示已校时，橙点表示校时暂不可用。声音和桌面通知仅在已校时时发送，多个周期同时收盘会合并提醒。", "Selected periods stay side by side and use Binance UTC candles. Green means synchronized; amber means synchronization is unavailable. Sound and desktop alerts require synchronized time; simultaneous closes share one alert."), 11, TextSecondary);
        hint.TextWrapping = TextWrapping.Wrap; hint.Margin = new Thickness(0, 0, 0, 12); content.Children.Add(hint);
        var options = new Border { Background = UI.Brush(Inset), CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 0, 16, 0), Margin = new Thickness(0, 0, 0, 12), Child = content };
        void Enable(bool on) { options.IsEnabled = on; options.Opacity = on ? 1 : .45; }
        Enable(enabled);
        Button arrow = null!;
        void Show() { options.Visibility = _candleCountdownOpen ? Visibility.Visible : Visibility.Collapsed; PointArrow(arrow, _candleCountdownOpen); }
        void Flip() { _candleCountdownOpen = !_candleCountdownOpen; Show(); }
        arrow = ChevronButton(Loc.T("收盘倒计时设置", "Candle countdown settings"), Flip);
        var heading = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(PartHandleWidth, 0, 0, 0) };
        heading.Children.Add(SettingLabel(Loc.T("收盘倒计时", "Candle countdown"))); heading.Children.Add(arrow);
        var toggle = new CheckBox { Style = (Style)FindResource("SettingsToggle"), IsChecked = enabled, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(toggle, Loc.T("收盘倒计时", "Candle countdown"));
        void Turn(bool on) { Change(s => { if (notch) s.NotchCandleCountdown = on; else s.CapsuleCandleCountdown = on; }); Enable(on); }
        toggle.Checked += (_, _) => Turn(true); toggle.Unchecked += (_, _) => Turn(false);
        var header = new Grid { MinHeight = 44, Background = Brushes.Transparent };
        header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(heading); Grid.SetColumn(toggle, 1); header.Children.Add(toggle);
        header.MouseLeftButtonDown += (_, e) => { Flip(); e.Handled = true; };
        Show(); var row = new StackPanel(); row.Children.Add(header); row.Children.Add(options); return row;
    }
}
