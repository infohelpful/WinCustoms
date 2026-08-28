using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinCustoms.Services;
using WinCustoms.ViewModels;

namespace WinCustoms.Views;

public sealed partial class DebloatPage : Page
{
    public DebloatViewModel ViewModel { get; }

    public DebloatPage()
    {
        ViewModel = App.GetService<DebloatViewModel>();
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (ViewModel.Packages.Count == 0 && !ViewModel.IsEmpty)
            await ViewModel.LoadCommand.ExecuteAsync(null);
    }

    private async void OnOpenStoreClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AppxPackageInfo package) return;
        await ViewModel.OpenStoreCommand.ExecuteAsync(package);
    }
}
