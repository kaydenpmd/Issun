using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IssunSpike;

public sealed partial class ConnectPage : UserControl
{
    public ConnectPage()
    {
        InitializeComponent();
        Loaded += (_, _) => SpikeLog.Write("[ui] page loaded");
    }

    public Grid RenderRoot => Root;
    public ProgressRing RingControl => Ring;
    public FrameworkElement StatusRow => (FrameworkElement)WaitingRow.Parent;

    public void SetConnected(bool connected)
    {
        WaitingRow.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        ConnectedRow.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        Ring.IsActive = !connected;
        SpikeLog.Write($"[ui] source row: {(connected ? "connected" : "waiting")}");
    }

    public void ShowReferenceInfoBar(bool show) =>
        RealInfoBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

    private void OnCopyKey(object sender, RoutedEventArgs e) => SpikeLog.Write("[ui] Copy key clicked");
}
