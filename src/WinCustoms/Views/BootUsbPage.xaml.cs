using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinCustoms.ViewModels;

namespace WinCustoms.Views;

public sealed partial class BootUsbPage : Page
{
    public BootUsbViewModel ViewModel { get; }

    public BootUsbPage()
    {
        ViewModel = App.GetService<BootUsbViewModel>();
        InitializeComponent();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BootUsbViewModel.ShowLocalAccountOptions)
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

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.StartDeviceWatch();
        _ = ViewModel.RefreshDisksCommand.ExecuteAsync(null);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.StopDeviceWatch();
        base.OnNavigatedFrom(e);
    }
}
