using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinCustoms.Common;
using WinCustoms.Services;
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
        await ViewModel.ScanCommand.ExecuteAsync(null);
    }

    private void OnTabSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var index = sender.Items.IndexOf(sender.SelectedItem);
        if (index >= 0) ViewModel.SelectedTabIndex = index;
    }

    private async void OnEntryToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch { DataContext: ShellMenuEntry entry } toggle) return;

        // Toggled 는 TwoWay 바인딩이 소스를 갱신하기 전에 올라올 수 있다.
        // 뷰모델이 한 박자 늦은 값을 보지 않도록 스위치에서 직접 읽어 맞춰 준다.
        entry.IsEnabled = toggle.IsOn;

        await ViewModel.ToggleEntryCommand.ExecuteAsync(entry);
    }

    private async void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CustomContextMenuEntry entry) return;
        await ViewModel.RemoveCommand.ExecuteAsync(entry);
    }
}
