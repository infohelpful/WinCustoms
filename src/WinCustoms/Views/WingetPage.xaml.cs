using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;
using WinCustoms.ViewModels;

namespace WinCustoms.Views;

public sealed partial class WingetPage : Page
{
    public WingetViewModel ViewModel { get; }

    public WingetPage()
    {
        ViewModel = App.GetService<WingetViewModel>();
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (ViewModel.Packages.Count == 0 && string.IsNullOrEmpty(ViewModel.StatusMessage))
            await ViewModel.LoadCommand.ExecuteAsync(null);
    }

    private void OnTabSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var text = sender.SelectedItem?.Text;
        if (text == "검색")
            ViewModel.SelectSearchTab();
        else
            ViewModel.SelectCatalogTab();
    }

    private async void OnSearchBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await ViewModel.SearchCommand.ExecuteAsync(null);
    }
}
