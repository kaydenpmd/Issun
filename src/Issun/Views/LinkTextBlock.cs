using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Issun.Platform;
using Issun.Presentation;

namespace Issun.Views;

/// <summary>
/// <c>v:LinkTextBlock.Text="{Binding FunnelOutput}"</c> on a TextBlock: the
/// text, with every http(s) address in it made clickable. For command output
/// whose next step is a link — `tailscale funnel` on a tailnet that hasn't
/// allowed Funnel answers with the admin page that allows it.
/// </summary>
public static class LinkTextBlock
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(LinkTextBlock), new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject d) => (string?)d.GetValue(TextProperty);

    public static void SetText(DependencyObject d, string? value) => d.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
            return;
        block.Inlines.Clear();
        if (e.NewValue is not string text || text.Length == 0)
            return;

        foreach (var part in LinkText.Split(text))
        {
            if (part.Link is { } uri)
            {
                var link = new Hyperlink(new Run(part.Text)) { NavigateUri = uri, ToolTip = uri.AbsoluteUri };
                link.RequestNavigate += (_, args) =>
                {
                    DesktopShell.OpenLink(args.Uri);
                    args.Handled = true;
                };
                block.Inlines.Add(link);
            }
            else
            {
                block.Inlines.Add(new Run(part.Text));
            }
        }
    }
}
