using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Promptarium.Services;

namespace Promptarium;

public sealed class DiagnosticsWindow : Window
{
    private static readonly SolidColorBrush WindowBackground = new(System.Windows.Media.Color.FromRgb(246, 248, 252));
    private static readonly SolidColorBrush TextForeground = new(System.Windows.Media.Color.FromRgb(23, 32, 51));
    private static readonly SolidColorBrush InputBackground = new(System.Windows.Media.Color.FromRgb(255, 255, 255));
    private static readonly SolidColorBrush InputBorder = new(System.Windows.Media.Color.FromRgb(114, 129, 153));
    private static readonly SolidColorBrush ButtonBackground = new(System.Windows.Media.Color.FromRgb(48, 56, 70));
    private static readonly SolidColorBrush ButtonHoverBackground = new(System.Windows.Media.Color.FromRgb(64, 81, 106));
    private static readonly SolidColorBrush ButtonForeground = new(System.Windows.Media.Color.FromRgb(244, 246, 251));
    private readonly System.Windows.Controls.TextBox _content = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        MinHeight = 380,
        Background = InputBackground,
        Foreground = TextForeground,
        BorderBrush = InputBorder,
        CaretBrush = TextForeground,
        Padding = new Thickness(8)
    };

    public DiagnosticsWindow()
    {
        Title = "Promptarium 診断ログ";
        Width = 900;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = WindowBackground;
        Foreground = TextForeground;
        var buttonStyle = CreateButtonStyle();
        var refresh = new System.Windows.Controls.Button { Content = "更新", Style = buttonStyle, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 6, 12, 6) };
        refresh.Click += (_, _) => RefreshContent();
        var copy = new System.Windows.Controls.Button { Content = "コピー", Style = buttonStyle, Padding = new Thickness(12, 6, 12, 6) };
        copy.Click += (_, _) => System.Windows.Clipboard.SetText(_content.Text);
        var panel = new DockPanel { Margin = new Thickness(14) };
        var actions = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        actions.Children.Add(refresh);
        actions.Children.Add(copy);
        DockPanel.SetDock(actions, Dock.Top);
        panel.Children.Add(actions);
        panel.Children.Add(_content);
        Content = panel;
        Loaded += (_, _) => RefreshContent();
    }

    private void RefreshContent() => _content.Text = AppLogger.ReadRecent();

    private static Style CreateButtonStyle()
    {
        var style = new Style(typeof(System.Windows.Controls.Button));
        style.Setters.Add(new Setter(System.Windows.Controls.Control.BackgroundProperty, ButtonBackground));
        style.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty, ButtonForeground));
        style.Setters.Add(new Setter(System.Windows.Controls.Control.BorderBrushProperty, InputBorder));
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(System.Windows.Controls.Control.BackgroundProperty, ButtonHoverBackground));
        style.Triggers.Add(hover);
        return style;
    }
}
