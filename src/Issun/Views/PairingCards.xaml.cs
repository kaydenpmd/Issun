using Issun.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Issun.Views;

public sealed partial class PairingCards : UserControl
{
    public PairingCards(PairingViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public PairingViewModel ViewModel { get; }
}
