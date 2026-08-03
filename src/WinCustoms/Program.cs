using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinCustoms.Common;

namespace WinCustoms;

/// <summary>
/// 직접 작성한 진입점(csproj 의 DISABLE_XAML_GENERATED_MAIN).
///
/// 승격 작업으로 실행된 경우에는 XAML 런타임을 아예 초기화하지 않는다.
/// 덕분에 UAC 승인 후 창이 깜빡이지 않고 수십 밀리초 안에 처리가 끝난다.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // DISM/powercfg 등 OEM(CP949) 콘솔 출력을 디코딩하려면 코드 페이지 제공자가 필요하다.
        ConsoleEncoding.EnsureRegistered();

        if (ElevatedJobHost.IsJobInvocation(args))
            return ElevatedJobHost.Run(args);

        if (SystemImageJobHost.IsJobInvocation(args))
            return SystemImageJobHost.Run(args);

        if (CustomIsoJobHost.IsJobInvocation(args))
            return CustomIsoJobHost.Run(args);

        if (BootUsbJobHost.IsJobInvocation(args))
            return BootUsbJobHost.Run(args);

        // UI 는 한 프로세스만. 이미 떠 있으면 기존 창을 앞으로 가져오고 종료.
        if (!SingleInstance.TryAcquire())
        {
            SingleInstance.ActivateExistingWindow();
            return 0;
        }

        // 이전에 실패/강제종료로 남은 ISO 추출본 정리 (C: 용량)
        try { WinCustomsWorkCleanup.PurgeStaleWorkFolders(); } catch { /* */ }
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { WinCustomsWorkCleanup.PurgeStaleWorkFolders(); } catch { /* */ }
        };

        // fail-fast 로 사라지기 전에 예외를 파일에 남긴다.
        CrashLog.BeginStartupCapture();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) CrashLog.Write("unhandled", ex);
        };

        WinRT.ComWrappersSupport.InitializeComWrappers();

        try
        {
            Application.Start(callbackParams =>
            {
                var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcherQueue));

                // App 인스턴스는 XAML 런타임이 보관하므로 지역 변수에 담아 둘 필요가 없다.
                new App();
            });
        }
        catch (Exception ex)
        {
            CrashLog.Write("Application.Start", ex);
            return 1;
        }

        return 0;
    }
}
