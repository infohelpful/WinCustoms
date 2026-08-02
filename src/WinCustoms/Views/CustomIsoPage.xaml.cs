using Microsoft.UI.Xaml.Controls;
using WinCustoms.ViewModels;

namespace WinCustoms.Views;

public sealed partial class CustomIsoPage : Page
{
    public CustomIsoViewModel ViewModel { get; }

    public CustomIsoPage()
    {
        ViewModel = App.GetService<CustomIsoViewModel>();
        InitializeComponent();
    }
}
