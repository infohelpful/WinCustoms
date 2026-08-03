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

    public MainWindow()
    {
        ViewModel = App.GetService<MainViewModel>();

        InitializeComponent();

        ConfigureTitleBar();
        ConfigureWindowIcon();
        ConfigureBackdrop();

        RootGrid.Loaded += OnRootLoaded;
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
            if (args.DidSizeChange) UpdateCaptionButtonInset();
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

        CaptionButtonsColumn.Width = new GridLength(AppWindow.TitleBar.RightInset / scale);
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
