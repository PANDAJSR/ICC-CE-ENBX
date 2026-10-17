using System.Windows;
using System.Windows.Controls;
using iNKORE.UI.WPF.Modern.Common.IconKeys;
using ModernControls = iNKORE.UI.WPF.Modern.Controls;

namespace ICC.CE.ENBX;

public sealed class PluginSettingsControl : UserControl
{
    private readonly Plugin _plugin;
    private readonly ModernControls.ToggleSwitch _clickToNextSwitch;
    private readonly ModernControls.ToggleSwitch _swipeNavigationSwitch;

    public PluginSettingsControl(Plugin plugin)
    {
        _plugin = plugin;

        var rootGrid = new Grid
        {
            Margin = new Thickness(35, 0, 35, 0)
        };

        var stackPanel = new StackPanel
        {
            MaxWidth = 1000,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        // Section Title: ENBX播放器
        var sectionTitle = new TextBlock
        {
            Text = "ENBX播放器",
            Margin = new Thickness(1, 16, 0, 8),
            FontWeight = FontWeights.SemiBold,
            FontSize = 14
        };
        sectionTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        stackPanel.Children.Add(sectionTitle);

        // Card 1: 单击背景翻页
        var cardClick = new ModernControls.SettingsCard
        {
            Header = "单击背景翻页",
            Description = "在幻灯片空白背景处单击可直接翻到下一页或触发下一个动画",
            Margin = new Thickness(0, 0, 0, 4)
        };
        cardClick.HeaderIcon = new ModernControls.FontIcon(SegoeFluentIcons.TouchPointer);

        _clickToNextSwitch = new ModernControls.ToggleSwitch
        {
            IsOn = _plugin.Settings.ClickToNextEnabled
        };
        _clickToNextSwitch.Toggled += OnClickToNextToggled;
        cardClick.Content = _clickToNextSwitch;
        stackPanel.Children.Add(cardClick);

        // Card 2: 滑动翻页
        var cardSwipe = new ModernControls.SettingsCard
        {
            Header = "滑动翻页",
            Description = "在触摸屏或使用鼠标拖拽滑动时切换幻灯片页面",
            Margin = new Thickness(0, 0, 0, 4)
        };
        cardSwipe.HeaderIcon = new ModernControls.FontIcon(SegoeFluentIcons.Swipe);

        _swipeNavigationSwitch = new ModernControls.ToggleSwitch
        {
            IsOn = _plugin.Settings.SwipeNavigationEnabled
        };
        _swipeNavigationSwitch.Toggled += OnSwipeNavigationToggled;
        cardSwipe.Content = _swipeNavigationSwitch;
        stackPanel.Children.Add(cardSwipe);

        rootGrid.Children.Add(stackPanel);
        Content = rootGrid;
    }

    private void OnClickToNextToggled(object sender, RoutedEventArgs e)
    {
        _plugin.Settings.ClickToNextEnabled = _clickToNextSwitch.IsOn;
        _plugin.SaveSettings();
    }

    private void OnSwipeNavigationToggled(object sender, RoutedEventArgs e)
    {
        _plugin.Settings.SwipeNavigationEnabled = _swipeNavigationSwitch.IsOn;
        _plugin.SaveSettings();
    }
}
