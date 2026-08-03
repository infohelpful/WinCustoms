using System.Diagnostics;

namespace WinCustoms.Common;

/// <summary>
/// ISO/부팅USB 임시 추출본 정리. 창 없이, 대기 없이(_trash_ rename + rd 백그라운드).
/// ProgramData / LocalAppData / Temp 어디에 남았든 싹 지운다.
/// </summary>
public static class WinCustomsWorkCleanup
{
    public static string ProgramDataRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "WinCustoms");

    public static string LocalAppDataRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinCustoms");

    public static string TempRoot =>
        Path.Combine(Path.GetTempPath(), "WinCustoms");

    /// <summary>새 작업은 여기만 쓴다 (공백 없음, 관리자 쓰기 안정).</summary>
    public static string CreateJobWorkDirectory(string leaf)
    {
        var dir = Path.Combine(ProgramDataRoot, leaf, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void TryDeleteTree(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!IsSafeTempPath(path)) return;
        QuarantineAndForget(path);
    }

    /// <summary>앱 시작·종료 시: ISO 임시본이 있을 수 있는 모든 위치를 즉시 치움.</summary>
    public static void PurgeStaleWorkFolders()
    {
        foreach (var root in new[] { ProgramDataRoot, LocalAppDataRoot })
        {
            foreach (var leaf in new[] { "IsoBuild", "BootUsb" })
                QuarantineChildren(Path.Combine(root, leaf));

            // oscd 스테이징 (ProgramData 전용)
            QuarantineChildren(Path.Combine(root, "oscd"));
        }

        // %TEMP%\WinCustoms 전체 (iso-probe, job json 등)
        QuarantineAndForget(TempRoot);

        // 예전 경로
        try
        {
            var legacy = Path.Combine(
                Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\",
                "wc-oscd");
            QuarantineAndForget(legacy);
        }
        catch { /* */ }
    }

    private static void QuarantineChildren(string folder)
    {
        if (!Directory.Exists(folder)) return;
        string[] kids;
        try { kids = Directory.GetDirectories(folder); }
        catch { return; }

        foreach (var d in kids)
            QuarantineAndForget(d);

        // 파일도 남기지 않음
        try
        {
            foreach (var f in Directory.GetFiles(folder))
            {
                try
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                    File.Delete(f);
                }
                catch { /* */ }
            }
        }
        catch { /* */ }
    }

    private static bool IsSafeTempPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            foreach (var root in new[] { ProgramDataRoot, LocalAppDataRoot, TempRoot })
            {
                var r = Path.GetFullPath(root);
                if (full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(full, r, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // 예전 C:\wc-oscd
            var legacy = Path.GetFullPath(Path.Combine(
                Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "wc-oscd"));
            if (full.StartsWith(legacy, StringComparison.OrdinalIgnoreCase))
                return true;

            // 경로에 IsoBuild/BootUsb 가 들어간 경우만 (오삭제 방지)
            var norm = full.Replace('/', '\\');
            return norm.Contains(@"\WinCustoms\IsoBuild\", StringComparison.OrdinalIgnoreCase)
                   || norm.Contains(@"\WinCustoms\BootUsb\", StringComparison.OrdinalIgnoreCase)
                   || norm.Contains(@"\WinCustoms\oscd\", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void QuarantineAndForget(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;

        var target = path;
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!name.StartsWith("_trash_", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                {
                    var trash = Path.Combine(parent, "_trash_" + Guid.NewGuid().ToString("N"));
                    Directory.Move(path, trash);
                    target = trash;
                }
            }
            catch
            {
                target = path;
            }
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                Arguments = "/d /c start \"\" /b cmd /d /c rd /s /q \"" + target + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi)?.Dispose();
        }
        catch
        {
            try { Directory.Delete(target, recursive: true); }
            catch { /* */ }
        }
    }
}
