using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using BrimDeck.Core;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    private FrameworkElement MarketChartWindowControls()
    {
        var panel = new StackPanel();
        var displays = Native.Displays.Current();
        var entries = displays.Select(d => (Display: new ChosenDisplay(d.Id, d.Name),
            Label: Loc.T("显示器 ", "Display ") + d.Device.Replace(@"\\.\DISPLAY", "") + " · " + Native.Displays.Label(d, displays)
                + (d.Primary ? Loc.T(" · 主显示器", " · primary") : ""))).ToList();
        var legacy = displays.FirstOrDefault(d => d.Device.Equals(@"\\.\DISPLAY2", StringComparison.OrdinalIgnoreCase));
        var selected = S.MarketChartDisplay ?? new ChosenDisplay(legacy?.Id ?? @"\\.\DISPLAY2", legacy?.Name ?? Loc.T("显示器 2", "Display 2"));
        int index = entries.FindIndex(entry => entry.Display.Id.Equals(selected.Id, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            index = entries.Count;
            entries.Add((selected, (selected.Name.Length > 0 ? selected.Name : Loc.T("显示器", "Display")) + Loc.T("（未连接）", " (not connected)")));
        }
        var choice = new ComboBox { Style = (Style)FindResource("SettingsCombo"), Width = 320,
            ItemsSource = entries.Select(entry => entry.Label).ToArray(), SelectedIndex = index };
        AutomationProperties.SetName(choice, Loc.T("图表打开显示器", "Chart display")); PopupMotion.Attach(choice);
        choice.SelectionChanged += (_, _) =>
        {
            if (choice.SelectedIndex >= 0) Change(s => s.MarketChartDisplay = entries[choice.SelectedIndex].Display);
        };
        panel.Children.Add(Row(Loc.T("图表打开显示器", "Chart display"), choice));
        panel.Children.Add(Toggle(Loc.T("F11 全屏打开", "Open in F11 fullscreen"), S.MarketChartFullscreen,
            on => Change(s => s.MarketChartFullscreen = on)));
        var hint = TextLine(Loc.T("所有布局共用，下次打开时生效。关闭全屏时以普通最大化窗口打开。", "Shared by all workspaces; applies the next time a chart opens. With fullscreen off, the window is maximized."), 11, TextSecondary);
        hint.TextWrapping = TextWrapping.Wrap; panel.Children.Add(hint);
        return panel;
    }
}
