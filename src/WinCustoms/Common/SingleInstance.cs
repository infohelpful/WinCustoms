using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinCustoms.Common;

/// <summary>
/// UI 인스턴스는 프로세스 하나만 허용한다.
/// (--elevated-job / --system-image-job 등 백그라운드 잡은 별도 프로세스이므로 적용하지 않는다.)
/// </summary>
internal static class SingleInstance
{
    private const string MutexName = @"Local\WinCustoms.UI.SingleInstance";

    // GC 되지 않도록 프로세스 수명 동안 유지.
    private static Mutex? _mutex;

    public static bool TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return false;
        }

        _mutex = mutex;
        return true;
    }

    public static void ActivateExistingWindow()
    {
        var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            try
            {
                if (process.Id == current.Id)
                    continue;

                var handle = process.MainWindowHandle;
                if (handle == IntPtr.Zero)
                    handle = FindTopLevelWindow(process.Id);

                if (handle == IntPtr.Zero)
                    continue;

                if (IsIconic(handle))
                    ShowWindow(handle, SwRestore);

                AllowSetForegroundWindow(process.Id);
                SetForegroundWindow(handle);
                return;
            }
            catch
            {
                // 다음 후보 프로세스 시도
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static IntPtr FindTopLevelWindow(int processId)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd))
                return true;

            GetWindowThreadProcessId(hWnd, out var windowPid);
            if (windowPid != (uint)processId)
                return true;

            // 소유자 없는 최상위 창만 (툴팁/팝업 제외).
            if (GetWindow(hWnd, GwOwner) != IntPtr.Zero)
                return true;

            found = hWnd;
            return false;
        }, IntPtr.Zero);

        return found;
    }

    private const int SwRestore = 9;
    private const uint GwOwner = 4;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
}
