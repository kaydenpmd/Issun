using System.Windows;
using System.Windows.Controls;

namespace BaselineWpf;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        var app = new Application { ThemeMode = ThemeMode.Dark };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Connect your source", FontSize = 28 });
        panel.Children.Add(new TextBox { Text = "https://demo-pc.example-tailnet.ts.net/now-playing", Margin = new Thickness(0, 12, 0, 0) });
        panel.Children.Add(new Button { Content = "Copy", Margin = new Thickness(0, 12, 0, 0) });
        GC.KeepAlive(Issun.Core.IssunInfo.Display);
        app.Run(new Window { Title = "Issun", Width = 600, Height = 760, Content = panel });
    }
}
