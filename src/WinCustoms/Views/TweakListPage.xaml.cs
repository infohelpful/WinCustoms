using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinCustoms.Models;
using WinCustoms.ViewModels;

namespace WinCustoms.Views;

/// <summary>
/// 5개 카테고리가 공유하는 목록 페이지.
/// 네비게이션 파라미터로 받은 <see cref="TweakCategory"/> 에 맞는 뷰모델을 붙인다.
/// </summary>
public sealed partial class TweakListPage : Page
{
    public TweakPageViewModelBase? ViewModel { get; private set; }

    public TweakListPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (NavigationTags.ToCategory(e.Parameter as string) is not { } category) return;

        ViewModel = App.GetService<TweakPageViewModelLocator>().Resolve(category);
        ViewModel.EnsureLoaded();

        // ViewModel 이 생성자 이후에 결정되므로 x:Bind 를 다시 평가시킨다.
        Bindings.Update();
    }

    private async void OnRunActionClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        if ((sender as FrameworkElement)?.DataContext is not TweakItem item) return;

        // 기본 앱 정리는 목록 선택 UI 가 필요해 전용 페이지로 넘긴다.
        if (item.Id == "privacy.debloat")
        {
            Frame.Navigate(typeof(DebloatPage));
            return;
        }

        await ViewModel.RunActionCommand.ExecuteAsync(item);
    }

    private async void OnLearnMoreClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        if ((sender as FrameworkElement)?.DataContext is not TweakItem item) return;

        await ViewModel.OpenLinkCommand.ExecuteAsync(item);
    }
}
