using Microsoft.UI.Xaml;
using WinCustoms.Common;
using WinCustoms.ViewModels;

namespace WinCustoms;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = default!;

    public static MainWindow? Window { get; private set; }

    /// <summary>파일 피커 등 Win32 상호 운용이 필요한 API 에 넘길 창 핸들.</summary>
    public static nint WindowHandle { get; private set; }

    public static T GetService<T>() where T : notnull
        => (T)Services.GetService(typeof(T))!;

    public App()
    {
        Services = ServiceConfiguration.Build();
        InitializeComponent();

        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 작업 표시줄 아이콘 그룹핑용 ID. 언패키지 앱에서 권장된다.
        NativeMethods.SetCurrentProcessExplicitAppUserModelID("WinCustoms.Tweaker");

        Window = new MainWindow();
        WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(Window);

        // 저장된 테마는 창이 생긴 뒤에야 적용할 수 있다(루트 FrameworkElement 가 필요).
        SettingsViewModel.ApplyTheme(GetService<SettingsViewModel>().SelectedThemeIndex);

        Window.Activate();

        // 여기까지 왔으면 시작은 성공이다. 이후에는 내부에서 처리하는 예외까지
        // 전부 기록되지 않도록 시작 진단을 끈다.
        CrashLog.StopStartupCapture();
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // 트윅 실행 중 발생한 예외로 앱 전체가 죽는 일이 없도록 한다.
        CrashLog.Write("unhandled(XAML)", e.Exception);
        e.Handled = true;
    }
}
