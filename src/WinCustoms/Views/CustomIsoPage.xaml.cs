using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinCustoms.ViewModels;

namespace WinCustoms.Views;

public sealed partial class CustomIsoPage : Page
{
    public CustomIsoViewModel ViewModel { get; }

    public CustomIsoPage()
    {
        ViewModel = App.GetService<CustomIsoViewModel>();
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    // ViewModel 은 DI 싱글톤이라 Page 보다 오래 산다. 구독을 풀지 않으면 페이지를
    // 나갔다 들어올 때마다 이미 소멸된 이전 Page 의 AutoLogonPasswordBox 를 참조하는
    // 델리게이트가 계속 쌓이고, 나중에 그 중 하나라도 실행되면 WinUI3 가 즉시 종료된다.
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CustomIsoViewModel.ShowLocalAccountOptions)
            && !ViewModel.ShowLocalAccountOptions)
        {
            AutoLogonPasswordBox.Password = string.Empty;
        }
    }

    private void AutoLogonPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box)
            ViewModel.LocalAccountPassword = box.Password;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        base.OnNavigatedFrom(e);
    }
}
