using System.Runtime.InteropServices;
using System.Text;

namespace WinCustoms.Common;

/// <summary>
/// 관리자(High IL) 에서는 WinRT FileOpenPicker 가 동작하지 않는다.
/// comdlg32 GetOpenFileName / GetSaveFileName 은 승격 상태에서도 동작한다.
/// </summary>
internal static class NativeFileDialog
{
    public static string? OpenFile(
        IntPtr owner,
        string title,
        string filterDescription,
        string filterPattern,
        string? defaultExt = null)
        => ShowOpenSave(isSave: false, owner, title, filterDescription, filterPattern, null, defaultExt);

    public static string? SaveFile(
        IntPtr owner,
        string title,
        string filterDescription,
        string filterPattern,
        string? suggestedFileName,
        string? defaultExt = null)
        => ShowOpenSave(isSave: true, owner, title, filterDescription, filterPattern, suggestedFileName, defaultExt);

    public static string? PickFolder(IntPtr owner, string title)
    {
        var displayName = Marshal.AllocHGlobal(520 * 2);
        try
        {
            for (var i = 0; i < 520 * 2; i++)
                Marshal.WriteByte(displayName, i, 0);

            var bi = new BrowseInfo
            {
                hwndOwner = owner,
                pidlRoot = IntPtr.Zero,
                pszDisplayName = displayName,
                lpszTitle = title,
                ulFlags = BifReturnOnlyFsDirs | BifNewDialogStyle | BifEditBox,
                lpfn = IntPtr.Zero,
                lParam = IntPtr.Zero,
                iImage = 0
            };

            TryForeground(owner);
            var pidl = SHBrowseForFolderW(ref bi);
            if (pidl == IntPtr.Zero)
                return null;

            try
            {
                var path = new StringBuilder(capacity: 65536);
                return SHGetPathFromIDListW(pidl, path) ? NullIfEmpty(path.ToString()) : null;
            }
            finally
            {
                Marshal.FreeCoTaskMem(pidl);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(displayName);
        }
    }

    private static string? ShowOpenSave(
        bool isSave,
        IntPtr owner,
        string title,
        string filterDescription,
        string filterPattern,
        string? suggestedFileName,
        string? defaultExt)
    {
        const int maxChars = 65536;
        var filterPtr = AllocFilter(filterDescription, filterPattern);
        var filePtr = Marshal.AllocHGlobal(maxChars * 2);
        try
        {
            // 버퍼를 null 로 채운 뒤 제안 파일명 복사
            for (var i = 0; i < maxChars * 2; i++)
                Marshal.WriteByte(filePtr, i, 0);

            if (isSave && !string.IsNullOrWhiteSpace(suggestedFileName))
            {
                var bytes = Encoding.Unicode.GetBytes(suggestedFileName);
                var copy = Math.Min(bytes.Length, (maxChars - 1) * 2);
                Marshal.Copy(bytes, 0, filePtr, copy);
            }

            var ofn = new OpenFileName
            {
                lStructSize = Marshal.SizeOf<OpenFileName>(),
                hwndOwner = owner,
                hInstance = IntPtr.Zero,
                lpstrFilter = filterPtr,
                lpstrCustomFilter = IntPtr.Zero,
                nMaxCustFilter = 0,
                nFilterIndex = 1,
                lpstrFile = filePtr,
                nMaxFile = maxChars,
                lpstrFileTitle = IntPtr.Zero,
                nMaxFileTitle = 0,
                lpstrInitialDir = IntPtr.Zero,
                lpstrTitle = title,
                Flags = OfnExplorer | OfnPathMustExist | OfnNoChangeDir | OfnHidReadOnly
                        | (isSave ? OfnOverwritePrompt : OfnFileMustExist),
                nFileOffset = 0,
                nFileExtension = 0,
                lpstrDefExt = TrimDot(defaultExt),
                lCustData = IntPtr.Zero,
                lpfnHook = IntPtr.Zero,
                lpTemplateName = IntPtr.Zero,
                pvReserved = IntPtr.Zero,
                dwReserved = 0,
                FlagsEx = 0
            };

            TryForeground(owner);

            var ok = isSave ? GetSaveFileNameW(ref ofn) : GetOpenFileNameW(ref ofn);
            if (!ok)
            {
                var err = CommDlgExtendedError();
                if (err == 0)
                    return null; // 사용자 취소

                throw new InvalidOperationException($"파일 대화상자 오류 코드: 0x{err:X}");
            }

            return NullIfEmpty(Marshal.PtrToStringUni(filePtr));
        }
        finally
        {
            Marshal.FreeHGlobal(filePtr);
            Marshal.FreeHGlobal(filterPtr);
        }
    }

    private static void TryForeground(IntPtr owner)
    {
        try
        {
            if (owner == IntPtr.Zero) return;
            AllowSetForegroundWindow(AsFwAny);
            SetForegroundWindow(owner);
        }
        catch
        {
            // ignore
        }
    }

    private static IntPtr AllocFilter(string description, string pattern)
    {
        var text = $"{description}\0{pattern}\0모든 파일\0*.*\0\0";
        var bytes = Encoding.Unicode.GetBytes(text);
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return ptr;
    }

    private static string? TrimDot(string? ext)
    {
        if (string.IsNullOrWhiteSpace(ext)) return null;
        return ext.TrimStart('.');
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.TrimEnd('\0').Trim();

    private const int OfnExplorer = 0x00080000;
    private const int OfnFileMustExist = 0x00001000;
    private const int OfnPathMustExist = 0x00000800;
    private const int OfnOverwritePrompt = 0x00000002;
    private const int OfnNoChangeDir = 0x00000008;
    private const int OfnHidReadOnly = 0x00000004;
    private const int AsFwAny = -1;

    private const uint BifReturnOnlyFsDirs = 0x00000001;
    private const uint BifNewDialogStyle = 0x00000040;
    private const uint BifEditBox = 0x00000010;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public IntPtr lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        public IntPtr lpstrInitialDir;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BrowseInfo
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public IntPtr pszDisplayName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileNameW(ref OpenFileName ofn);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSaveFileNameW(ref OpenFileName ofn);

    [DllImport("comdlg32.dll")]
    private static extern int CommDlgExtendedError();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolderW(ref BrowseInfo bi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHGetPathFromIDListW(IntPtr pidl, StringBuilder pszPath);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
}
