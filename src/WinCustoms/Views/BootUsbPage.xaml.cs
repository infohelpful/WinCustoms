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
