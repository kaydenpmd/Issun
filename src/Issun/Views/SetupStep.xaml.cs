using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Issun.Views;

/// <summary>One of SetupView's pages, and the SetupStep showing it now, if any.</summary>
public sealed class StepSlot(UIElement step)
{
    public UIElement Step { get; } = step;
    internal SetupStep? Holder { get; set; }
}

/// <summary>
/// The Frame's page for one step of setup. The step itself is written in
/// SetupView.xaml, where x:Bind reaches it, and arrives here in a StepSlot as
/// the navigation parameter. Frame keeps no back stack (SetupView navigates
/// forwards both ways), so going back to a step finds it still the content
/// of the SetupStep that showed it before, and it is taken from there first.
/// The slot says which: FrameworkElement.Parent didn't (Back threw
/// "already the child of another element" in the sandbox, 13 Nov 2026).
/// </summary>
public sealed partial class SetupStep : Page
{
    public SetupStep()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not StepSlot slot)
            return;
        if (slot.Holder is { } old && !ReferenceEquals(old, this))
            old.Content = null;
        slot.Holder = this;
        Content = slot.Step;
    }
}
