using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using WinCustoms.Services;
using WinCustoms.ViewModels;
using WinCustoms.Views;

namespace WinCustoms;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    // 시스템 백업/커스텀 ISO/부팅 USB(Rufus+Ventoy)는 오래 걸리고 디스크를 직접
    // 건드리는 작업이라, 하나라도 진행 중이면 다른 메뉴로 못 넘어가게 막는다.
    // 이 넷은 전부 DI 싱글톤이고 MainWindow 도 앱 생명주기 내내 하나만 존재하므로
    // 구독을 따로 해제할 필요는 없다(BootUsbPage 처럼 Page 단위로 생성/소멸되는
    // 경우와 다름).
    private readonly SystemBackupViewModel _systemBackupVm;
    private readonly CustomIsoViewModel _customIsoVm;
    private readonly BootUsbViewModel _bootUsbVm;
    private readonly VentoyViewModel _ventoyVm;

    public MainWindow()
    {
        ViewModel = App.GetService<MainViewModel>();
        _systemBackupVm = App.GetService<SystemBackupViewModel>();
        _customIsoVm = App.GetService<CustomIsoViewModel>();
        _bootUsbVm = App.GetService<BootUsbViewModel>();
        _ventoyVm = App.GetService<VentoyViewModel>();

        InitializeComponent();

        ConfigureTitleBar();
        ConfigureWindowIcon();
        ConfigureBackdrop();

        _systemBackupVm.PropertyChanged += OnBusyRelatedViewModelPropertyChanged;
        _customIsoVm.PropertyChanged += OnBusyRelatedViewModelPropertyChanged;
        _bootUsbVm.PropertyChanged += OnBusyRelatedViewModelPropertyChanged;
        _ventoyVm.PropertyChanged += OnBusyRelatedViewModelPropertyChanged;

        RootGrid.Loaded += OnRootLoaded;
    }

    // 작업 완료 시 IsBusy=false 로 바뀌는 지점이 서비스 계층 어딘가의 ConfigureAwait(false)
    // 뒤라서(백그라운드 스레드 풀 스레드) 여기까지 UI 스레드가 아닌 채로 넘어올 수 있다.
    // NavView.IsEnabled 같은 XAML 요소를 다른 스레드에서 직접 건드리면 예외 없이 바로
    // 프로세스가 죽는다(Microsoft.UI.Windowing.dll 페일패스트, 실측 확인됨) — 반드시
    // DispatcherQueue 를 거친다.
    private void OnBusyRelatedViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != "IsBusy") return;

        if (DispatcherQueue.HasThreadAccess)
            UpdateNavEnabled();
        else
            DispatcherQueue.TryEnqueue(UpdateNavEnabled);
    }

    private void UpdateNavEnabled()
    {
        // NavView.IsEnabled 를 통째로 끄면 ContentFrame 도 같이 꺼진다 — ContentFrame 이
        // NavView 의 Content 로 그 안에 들어있기 때문(MainWindow.xaml). 그러면 작업 중인
        // 페이지 자체(스크롤, 취소 버튼, 로그)까지 다 먹통이 된다(실측 확인됨). 메뉴 항목만
        // 하나씩 잠가야 한다.
        var anyBusy = _systemBackupVm.IsBusy || _customIsoVm.IsBusy || _bootUsbVm.IsBusy || _ventoyVm.IsBusy;
        foreach (var item in NavView.MenuItems.OfType<NavigationViewItem>())
            item.IsEnabled = !anyBusy;
        foreach (var item in NavView.FooterMenuItems.OfType<NavigationViewItem>())
            item.IsEnabled = !anyBusy;
    }

    private void ConfigureTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);

        AppWindow.Title = "WinCustoms";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 820));

        UpdateCaptionButtonInset();
        AppWindow.Changed += (_, args) =>
        {
            // 여기서 예외가 새어나가면 네이티브 WinRT 이벤트 콜백 경계를 못 넘어가고
            // 그대로 RaiseFailFastException 으로 프로세스가 즉사한다(실제 크래시 덤프로
            // 확인됨: Microsoft_UI_Windowing!AppWindow::RaiseChanged ->
            // ThrowExceptionForHR -> FailWithException, 2026-09-18). 절대 여기서
            // 예외가 빠져나가면 안 된다.
            try
            {
                if (args.DidSizeChange) UpdateCaptionButtonInset();
            }
            catch { /* 창 상태 전환 중 일시적으로 TitleBar 등이 비어있을 수 있다 — 무시 */ }
        };
    }

    /// <summary>
    /// 작업 표시줄과 Alt+Tab 에 쓰이는 창 아이콘을 지정한다.
    /// exe 에 박아 둔 아이콘(&lt;ApplicationIcon&gt;)은 탐색기에만 반영되고
    /// WinUI 3 창에는 자동으로 적용되지 않는다.
    /// </summary>
    private void ConfigureWindowIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "WinCustoms.ico");
        if (!File.Exists(path)) return;

        AppWindow.SetIcon(path);
    }

    /// <summary>
    /// 캡션 버튼 영역만큼 오른쪽을 비워 둔다.
    /// RightInset 은 물리 픽셀이므로 현재 배율로 나눠 DIP 로 바꾼다.
    /// </summary>
    private void UpdateCaptionButtonInset()
    {
        var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        if (scale <= 0) scale = 1.0;

        // 창 상태가 바뀌는 도중(모니터 이동·DPI 변경 등)에는 TitleBar 가 일시적으로
        // null 일 수 있다 — 그대로 .RightInset 에 접근하면 NullReferenceException.
        var titleBar = AppWindow.TitleBar;
        if (titleBar is null) return;

        CaptionButtonsColumn.Width = new GridLength(titleBar.RightInset / scale);
    }

    /// <summary>
    /// Mica 는 Windows 11 이상에서만 지원된다.
    /// 지원되지 않으면 Acrylic 으로, 그것도 안 되면 XAML 기본 배경으로 자연스럽게 내려간다.
    /// </summary>
    private void ConfigureBackdrop()
    {
        if (MicaController.IsSupported()) return;

        SystemBackdrop = DesktopAcrylicController.IsSupported()
            ? new DesktopAcrylicBackdrop()
            : null;
    }

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        // ContentDialog 를 띄우려면 XamlRoot 가 필요하다.
        App.GetService<IDialogService>().XamlRoot = RootGrid.XamlRoot;

        // XamlRoot 가 생긴 뒤라야 정확한 배율로 캡션 버튼 폭을 계산할 수 있다.
        UpdateCaptionButtonInset();

        // 지난 실행에서 고른 테마를 복원한다.
        SettingsViewModel.ApplyTheme(App.GetService<SettingsViewModel>().SelectedThemeIndex);

        ViewModel.RefreshAll();

        NavView.SelectedItem = NavView.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(i => (string?)i.Tag == NavigationTags.Explorer);
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag }) return;

        ViewModel.SelectedTag = tag;
        Navigate(tag);
    }

    /// <summary>
    /// 페이지 안의 버튼 등에서 다른 메뉴로 옮길 때 쓴다(예: 부팅 USB → 커스텀 ISO).
    /// 왼쪽 네비게이션의 선택 표시도 같이 맞춰준다 — Frame.Navigate 만 직접 부르면
    /// 내용은 바뀌어도 왼쪽 메뉴는 이전 항목이 선택된 채로 남는다.
    /// </summary>
    public void NavigateTo(string tag)
    {
        var item = NavView.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(i => (string?)i.Tag == tag);
        if (item is not null)
            NavView.SelectedItem = item;

        ViewModel.SelectedTag = tag;
        Navigate(tag);
    }

    private void Navigate(string tag)
    {
        var transition = new EntranceNavigationTransitionInfo();

        // 네비게이션 파라미터는 문자열만 쓴다.
        // 커스텀 enum 을 넘기면 Native AOT 에서 CsWinRT 박싱이 실패할 수 있다.
        if (NavigationTags.ToCategory(tag) is not null)
        {
            ContentFrame.Navigate(typeof(TweakListPage), tag, transition);
            return;
        }

        var pageType = tag switch
        {
            NavigationTags.ContextMenu => typeof(ContextMenuEditorPage),
            NavigationTags.Debloat => typeof(DebloatPage),
            NavigationTags.Winget => typeof(WingetPage),
            NavigationTags.SystemBackup => typeof(SystemBackupPage),
            NavigationTags.CustomIso => typeof(CustomIsoPage),
            NavigationTags.BootUsb => typeof(BootUsbPage),
            NavigationTags.Settings => typeof(SettingsPage),
            _ => null
        };

        if (pageType is not null)
            ContentFrame.Navigate(pageType, null, transition);
    }
}
