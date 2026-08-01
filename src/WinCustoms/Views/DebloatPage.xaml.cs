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

        // IsEmpty 는 한 번이라도 읽어온 뒤에만 true 가 되므로, 결과가 0건이어도 재조회하지 않는다.
        if (ViewModel.Packages.Count == 0 && !ViewModel.IsEmpty)
            await ViewModel.LoadCommand.ExecuteAsync(null);
    }

    private void OnShowAllClick(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox box)
            ViewModel.HideUninstalled = box.IsChecked != true;
    }

    private async void OnOpenStoreClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AppxPackageInfo package) return;
        await ViewModel.OpenStoreCommand.ExecuteAsync(package);
    }
}
