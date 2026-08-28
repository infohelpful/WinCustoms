using Microsoft.UI.Xaml;
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
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CustomIsoViewModel.ShowLocalAccountOptions)
                && !ViewModel.ShowLocalAccountOptions)
            {
                AutoLogonPasswordBox.Password = string.Empty;
            }
        };
    }

    private void AutoLogonPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box)
            ViewModel.LocalAccountPassword = box.Password;
    }
}
