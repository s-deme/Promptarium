using System.Windows;
using System.Windows.Controls;
using Promptarium.Services;

namespace Promptarium;

public sealed class DiagnosticsWindow : Window
{
    private readonly System.Windows.Controls.TextBox _content = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        MinHeight = 380
    };

    public DiagnosticsWindow()
    {
        Title = "Promptarium 診断ログ";
        Width = 900;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var refresh = new System.Windows.Controls.Button { Content = "更新", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 6, 12, 6) };
        refresh.Click += (_, _) => RefreshContent();
        var copy = new System.Windows.Controls.Button { Content = "コピー", Padding = new Thickness(12, 6, 12, 6) };
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
}
