using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace WinCustoms.Common;

/// <summary>WinRE에 자동 복원 부트스트랩을 심고 다음 부팅을 복구 환경으로 보낸다.</summary>
public static class SystemImageAutoRestore
{
    public static void Prepare(SystemImageJobRequest request, Action<int?, string> progress)
    {
        if (!File.Exists(request.ImageFile))
            throw new FileNotFoundException("WIM 파일을 찾을 수 없습니다.", request.ImageFile);

        progress(5, "자동 복원 플래그 작성...");
        SystemImageCompanionFiles.WriteAutoRestoreFlag(request.ImageFile);

        progress(15, "WinRE 위치 확인...");
        RunTool("reagentc.exe", ["/enable"], ignoreExitCode: true);
        var winreWim = FindWinReWimPath();
        progress(25, "WinRE: " + winreWim);

        var mountDir = Path.Combine(Path.GetTempPath(), "WinCustoms", "winre-mount-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mountDir);
        var mounted = false;
        var dism = ResolveDismPath();

        try
        {
            if (!string.IsNullOrWhiteSpace(request.CancelFile) && File.Exists(request.CancelFile))
                throw new OperationCanceledException();

            progress(35, "WinRE 이미지 마운트 중...");
            RunTool(dism,
            [
                "/Mount-Image",
                $"/ImageFile:{winreWim}",
                "/Index:1",
                $"/MountDir:{mountDir}"
            ], ignoreExitCode: false);
            mounted = true;

            if (!string.IsNullOrWhiteSpace(request.CancelFile) && File.Exists(request.CancelFile))
                throw new OperationCanceledException();

            progress(55, "자동 복원 부트스트랩 주입...");
            InjectWinReBootstrap(mountDir);

            progress(75, "WinRE 이미지 저장 중...");
            RunTool(dism,
            [
                "/Unmount-Image",
                $"/MountDir:{mountDir}",
                "/Commit"
            ], ignoreExitCode: false);
            mounted = false;

            progress(90, "다음 부팅을 WinRE로 설정...");
            try
            {
                RunTool("reagentc.exe", ["/boottorecovery"], ignoreExitCode: false);
            }
            catch (Exception ex)
            {
                progress(null, "reagentc /boottorecovery 실패(고급 시작으로 대체 가능): " + ex.Message);
            }
        }
        finally
        {
            if (mounted)
            {
                try
                {
                    RunTool(dism,
                    [
                        "/Unmount-Image",
                        $"/MountDir:{mountDir}",
                        "/Discard"
                    ], ignoreExitCode: true);
                }
                catch
                {
                    // ignore
                }
            }

            try
            {
                if (Directory.Exists(mountDir))
                    Directory.Delete(mountDir, recursive: true);
            }
            catch
            {
                // ignore
            }
        }
    }

    private static void InjectWinReBootstrap(string mountDir)
    {
        var system32 = Path.Combine(mountDir, "Windows", "System32");
        if (!Directory.Exists(system32))
            throw new InvalidOperationException("마운트된 WinRE에서 Windows\\System32 를 찾지 못했습니다.");

        var bootstrapPath = Path.Combine(system32, SystemImageCompanionFiles.WinReBootstrapFileName);
        File.WriteAllText(bootstrapPath, SystemImageCompanionFiles.BuildWinReBootstrapScript(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var winpeshl = Path.Combine(system32, "winpeshl.ini");
        var bak = Path.Combine(system32, "winpeshl.ini.wincustoms.bak");
        if (File.Exists(winpeshl) && !File.Exists(bak))
            File.Copy(winpeshl, bak, overwrite: false);

        var ini = "[LaunchApps]\r\n%SYSTEMROOT%\\System32\\" + SystemImageCompanionFiles.WinReBootstrapFileName + "\r\n";
        File.WriteAllText(winpeshl, ini, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string FindWinReWimPath()
    {
        var info = RunTool("reagentc.exe", ["/info"], ignoreExitCode: true);
        var text = info.StdOut + "\n" + info.StdErr;

        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.IndexOf("Winre.wim", StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            var start = line.IndexOf(@"\\?\GLOBALROOT", StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                var drive = line.IndexOf(":\\", StringComparison.Ordinal);
                if (drive > 0) start = drive - 1;
            }

            if (start < 0) continue;

            var path = line[start..].Trim();
            var end = path.IndexOf("Winre.wim", StringComparison.OrdinalIgnoreCase);
            if (end >= 0)
                path = path[..(end + "Winre.wim".Length)];

            path = path.Trim().TrimEnd('.', ' ', '\t');
            if (File.Exists(path))
                return path;

            if (Directory.Exists(path))
            {
                var candidate = Path.Combine(path, "Winre.wim");
                if (File.Exists(candidate)) return candidate;
            }

            if (path.Contains("GLOBALROOT", StringComparison.OrdinalIgnoreCase)
                && path.EndsWith("Winre.wim", StringComparison.OrdinalIgnoreCase))
                return path;
        }

        var locationMatch = Regex.Match(text,
            @"(?:Windows RE location|Windows RE 위치)\s*:\s*(.+)",
            RegexOptions.IgnoreCase);
        if (locationMatch.Success)
        {
            var loc = locationMatch.Groups[1].Value.Trim();
            var candidate = Path.Combine(loc.TrimEnd('\\'), "Winre.wim");
            if (File.Exists(candidate)) return candidate;
            if (loc.Contains("GLOBALROOT", StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                var candidate = Path.Combine(drive.RootDirectory.FullName, "Recovery", "WindowsRE", "Winre.wim");
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // skip
            }
        }

        throw new InvalidOperationException(
            "WinRE 이미지(Winre.wim)를 찾지 못했습니다. 복구 환경이 활성화되어 있는지 확인하세요.\n"
            + Truncate(text, 500));
    }

    private static string ResolveDismPath()
    {
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var dism = Path.Combine(system32, "dism.exe");
        return File.Exists(dism) ? dism : "dism.exe";
    }

    private static (int ExitCode, string StdOut, string StdErr) RunTool(
        string fileName, IReadOnlyList<string> args, bool ignoreExitCode)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException($"{fileName} 을(를) 시작할 수 없습니다.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(300_000);

        if (!ignoreExitCode && process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException($"{fileName} 종료 코드 {process.ExitCode}. {detail.Trim()}");
        }

        return (process.ExitCode, stdout, stderr);
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}
