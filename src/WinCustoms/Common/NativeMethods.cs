using System.Runtime.InteropServices;

namespace WinCustoms.Common;

internal static partial class NativeMethods
{
    internal const int SHCNE_ASSOCCHANGED = 0x08000000;
    internal const uint SHCNF_IDLIST = 0x0000;
    internal const uint SHCNF_FLUSH = 0x1000;

    internal const uint WM_SETTINGCHANGE = 0x001A;
    internal const nint HWND_BROADCAST = 0xFFFF;
    internal const uint SMTO_ABORTIFHUNG = 0x0002;

    /// <summary>셸에 파일 연결/컨텍스트 메뉴 변경을 통지한다. 탐색기 재시작 없이 반영되는 경우가 많다.</summary>
    [LibraryImport("shell32.dll", EntryPoint = "SHChangeNotify")]
    internal static partial void SHChangeNotify(int wEventId, uint uFlags, nint dwItem1, nint dwItem2);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint SendMessageTimeout(
        nint hWnd, uint msg, nint wParam, string lParam, uint flags, uint timeoutMs, out nint result);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SystemParametersInfo(uint action, uint param, nint pvParam, uint winIni);

    /// <summary>작업 표시줄 점프 리스트/아이콘 그룹핑을 위한 AppUserModelID. 언패키지 앱에서 권장된다.</summary>
    [LibraryImport("shell32.dll", EntryPoint = "SetCurrentProcessExplicitAppUserModelID", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SetCurrentProcessExplicitAppUserModelID(string appId);

    [LibraryImport("shlwapi.dll", EntryPoint = "SHLoadIndirectString", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int SHLoadIndirectString(string source, char* buffer, int bufferLength, nint reserved);

    /// <summary>
    /// <c>@shell32.dll,-8506</c> 형태의 간접 문자열을 실제 표시 문자열로 바꾼다.
    /// 셸 동사의 MUIVerb 는 대부분 이 형식이라 원문 그대로 쓰면 리소스 참조가 화면에 노출된다.
    /// </summary>
    internal static unsafe string? LoadIndirectString(string source)
    {
        const int capacity = 512;
        var buffer = stackalloc char[capacity];

        return SHLoadIndirectString(source, buffer, capacity, 0) == 0
            ? new string(buffer)
            : null;
    }

    internal static void BroadcastSettingChange(string area)
        => SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, 0, area, SMTO_ABORTIFHUNG, 1000, out _);

    internal static void NotifyShellAssociationChanged()
        => SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST | SHCNF_FLUSH, 0, 0);
}
