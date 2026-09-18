using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinCustoms.ViewModels;

namespace WinCustoms.Views;

public sealed partial class BootUsbPage : Page
{
    public BootUsbViewModel ViewModel { get; }
    public VentoyViewModel VentoyViewModel { get; }

    public BootUsbPage()
    {
        ViewModel = App.GetService<BootUsbViewModel>();
        VentoyViewModel = App.GetService<VentoyViewModel>();
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    // ViewModel 은 DI 싱글톤이라 Page 보다 오래 산다. 구독을 풀지 않으면 페이지를
    // 나갔다 들어올 때마다 이미 소멸된 이전 Page 의 AutoLogonPasswordBox 를 참조하는
    // 델리게이트가 계속 쌓이고, 나중에 그 중 하나라도 실행되면 WinUI3 가 즉시 종료된다.
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BootUsbViewModel.ShowLocalAccountOptions)
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

    // ToggleButton은 이미 선택된 걸 다시 누르면 기본 동작으로 체크가 풀려버린다.
    // 탭은 "현재 뭐가 선택돼 있는지" 표시하는 용도라 항상 정확한 상태로 되돌려 준다.
    private void OnRufusTabClick(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectRufusTab();
        RufusTabButton.IsChecked = true;
        VentoyTabButton.IsChecked = false;
    }

    private void OnVentoyTabClick(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectVentoyTab();
        VentoyTabButton.IsChecked = true;
        RufusTabButton.IsChecked = false;
    }

    private void OnGoToCustomIsoClick(object sender, RoutedEventArgs e)
        => App.Window?.NavigateTo(ViewModels.NavigationTags.CustomIso);

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.StartDeviceWatch();
        _ = ViewModel.RefreshDisksCommand.ExecuteAsync(null);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.StopDeviceWatch();
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        base.OnNavigatedFrom(e);
    }
}
