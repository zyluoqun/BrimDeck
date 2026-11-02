using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    private FrameworkElement MarketRankingChoice()
    {
        var panel = new StackPanel();
        panel.Children.Add(TextLine(Loc.T("行情榜单（可多选）", "Market rankings (select multiple)"), 13, TextPrimary, FontWeights.SemiBold));
        var hint = TextLine(Loc.T("按下列顺序滚动显示，每个榜单独立设置 1–50 个币种，默认 20 个。", "Scrolls in the order below. Each board shows 1–50 coins, with 20 by default."), 11, TextSecondary);
        hint.TextWrapping = TextWrapping.Wrap; hint.Margin = new Thickness(0, 4, 0, 4); panel.Children.Add(hint);
        foreach (var ranking in MarketBoards.Order)
        {
            string title = MarketPageView.Label(ranking);
            var box = new CheckBox { Style = (Style)FindResource("SettingsToggle"), IsChecked = S.SelectedMarketRankings.Contains(ranking),
                Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(box, Loc.T("行情榜单 ", "Market ranking ") + title);
            void Select(bool on) => Change(s =>
            {
                s.MarketRankings = s.SelectedMarketRankings.Where(value => value != ranking).ToList();
                if (on) s.MarketRankings.Add(ranking);
            });
            box.Checked += (_, _) => Select(true); box.Unchecked += (_, _) => Select(false);
            var controls = NumericControls(Loc.T(title + "显示数量", title + " coin count"), S.MarketCount(ranking), 1, 50, 1, Loc.T("个", "coins"),
                value => Change(s => s.MarketCounts[ranking] = (int)value));
            var row = new Grid { Height = 44 };
            row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new());
            var label = TextLine(title, 13, TextPrimary); label.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(label); Grid.SetColumn(controls.Number, 1); row.Children.Add(controls.Number);
            Grid.SetColumn(box, 2); row.Children.Add(box); panel.Children.Add(row);
        }
        return panel;
    }
    private void MarketSizeControls()
    {
        var dimensions = new Grid { Margin = new Thickness(0, 8, 0, 0) }; dimensions.ColumnDefinitions.Add(new()); dimensions.ColumnDefinitions.Add(new());
        var width = TextLine(Loc.T("宽度随行情内容自动调整", "Width automatically fits the market content"), 12, TextSecondary);
        width.Margin = new Thickness(0, 0, 20, 0); dimensions.Children.Add(width);
        var height = Dimension(Loc.T("高度", "Height"), S.MarketHeight, DeckPages.MinHeight, DeckPages.MaxHeight,
            value => Resize(s => s.MarketHeight = value, DeckPage.Market)); height.Margin = new Thickness(20, 0, 0, 0); Grid.SetColumn(height, 1); dimensions.Children.Add(height);
        Target.Children.Add(dimensions);
        Target.Children.Add(TextLine(Loc.T("展开页与收起摘要独立设置。刘海、胶囊和指标条的摘要开关在收起外观中。", "Expanded pages and compact summaries are independent. Configure the notch, capsule and indicator summaries under collapsed appearance."), 11, TextSecondary));
        MarketChartControls();
    }
}
