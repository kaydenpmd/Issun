using Issun.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Issun.Views;

public sealed partial class StatusDot : UserControl
{
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(object), typeof(StatusDot), new PropertyMetadata(null, (d, _) => ((StatusDot)d).Apply()));

    public StatusDot()
    {
        InitializeComponent();
        Loaded += (_, _) => Apply();
    }

    /// <summary>A <see cref="StatusLine"/>; its level picks the colour.</summary>
    public object? Status
    {
        get => GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    private void Apply()
    {
        var level = (Status as StatusLine)?.Level ?? StatusLevel.Neutral;
        VisualStateManager.GoToState(this, level.ToString(), false);
    }
}
