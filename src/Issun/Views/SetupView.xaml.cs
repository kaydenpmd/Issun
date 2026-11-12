using Issun.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Issun.Views;

public sealed partial class SetupView : UserControl
{
    public SetupView(SetupViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public SetupViewModel ViewModel { get; }
}
