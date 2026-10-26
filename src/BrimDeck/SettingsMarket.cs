using System.Windows;
using System.Windows.Controls;
using BrimDeck.Core;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    private void MarketSizeControls()
    {
        var dimensions = new Grid { Margin = new Thickness(0, 8, 0, 0) }; dimensions.ColumnDefinitions.Add(new()); dimensions.ColumnDefinitions.Add(new());
        var width = Dimension(Loc.T("宽度", "Width"), S.MarketWidth, DeckPages.MinWidth, DeckPages.MaxWidth,
            value => Resize(s => s.MarketWidth = value, DeckPage.Market)); width.Margin = new Thickness(0, 0, 20, 0); dimensions.Children.Add(width);
        var height = Dimension(Loc.T("高度", "Height"), S.MarketHeight, DeckPages.MinHeight, DeckPages.MaxHeight,
            value => Resize(s => s.MarketHeight = value, DeckPage.Market)); height.Margin = new Thickness(20, 0, 0, 0); Grid.SetColumn(height, 1); dimensions.Children.Add(height);
        Target.Children.Add(dimensions);
        Target.Children.Add(TextLine(Loc.T("展开页与收起摘要独立设置。刘海、胶囊和指标条的摘要开关在收起外观中。", "Expanded pages and compact summaries are independent. Configure the notch, capsule and indicator summaries under collapsed appearance."), 11, TextSecondary));
    }
}
