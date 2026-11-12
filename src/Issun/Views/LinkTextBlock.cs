using Issun.Platform;
using Issun.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace Issun.Views;

/// <summary>
/// <c>v:LinkTextBlock.Text="{x:Bind ViewModel.FunnelOutput}"</c> on a TextBlock:
/// the text, with every http(s) address in it made clickable. For command
/// output whose next step is a link — `tailscale funnel` on a tailnet that
/// hasn't allowed Funnel answers with the admin page that allows it.
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
                // No NavigateUri: Click opens it through DesktopShell, which
                // refuses anything but http(s) and logs a failure.
                var link = new Hyperlink();
                link.Inlines.Add(new Run { Text = part.Text });
                ToolTipService.SetToolTip(link, uri.AbsoluteUri);
                link.Click += (_, _) => DesktopShell.OpenLink(uri);
                block.Inlines.Add(link);
            }
            else
            {
                block.Inlines.Add(new Run { Text = part.Text });
            }
        }
    }
}
