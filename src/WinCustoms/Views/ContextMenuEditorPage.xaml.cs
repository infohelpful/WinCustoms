using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinCustoms.Common;
using WinCustoms.ViewModels;

namespace WinCustoms.Views;

public sealed partial class ContextMenuEditorPage : Page
{
    public ContextMenuEditorViewModel ViewModel { get; }

    public ContextMenuEditorPage()
    {
        ViewModel = App.GetService<ContextMenuEditorViewModel>();
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadCommand.ExecuteAsync(null);
    }

    private async void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CustomContextMenuEntry entry) return;
        await ViewModel.RemoveCommand.ExecuteAsync(entry);
    }
}
