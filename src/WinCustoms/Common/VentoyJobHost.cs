using System.Diagnostics;
using System.Text.Json;

namespace WinCustoms.Common;

/// <summary>
/// Ventoy 스타일 부팅 USB 설치. 공식 Ventoy2Disk.exe(Tools\ventoy\)를 VTOYCLI 명령줄
/// 모드로 실행한다 — Ventoy 소스를 재구현하지 않고 diskpart/DISM/oscdimg 와 같은 방식으로
/// 외부 프로그램을 그대로 호출한다(Tools\ventoy\NOTICE.md 참고). 승격 프로세스에서
/// XAML 없이 실행된다.
/// </summary>
public static class VentoyJobHost
{
    public const string JobSwitch = "--ventoy-job";

    public static bool IsJobInvocation(string[] args) => TryGetJobPath(args, out _);

    public static bool TryGetJobPath(string[] args, out string jobPath)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], JobSwitch, StringComparison.Ordinal))
            {
                jobPath = args[i + 1];
                return true;
            }
        }

        jobPath = string.Empty;
        return false;
    }

    public static int Run(string[] args)
    {
        if (!TryGetJobPath(args, out var jobPath))
            return 2;

        VentoyInstallJobRequest? request = null;
        var result = new VentoyInstallJobResult();

        try
        {
            var json = File.ReadAllText(jobPath);
            request = JsonSerializer.Deserialize(json, WinCustomsJsonContext.Default.VentoyInstallJobRequest)
                      ?? throw new InvalidOperationException("Ventoy 설치 작업 파일을 해석할 수 없습니다.");

            Install(request, result);
            result.Success = true;
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.Error = "작업이 취소되었습니다.";
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
        }

        try
        {
            var resultPath = request?.ResultFile ?? (jobPath + ".result");
            File.WriteAllText(resultPath, JsonSerializer.Serialize(result, WinCustomsJsonContext.Default.VentoyInstallJobResult));
        }
        catch
        {
            // ignore
        }

        return result.Success ? 0 : 1;
    }

    private static void Install(VentoyInstallJobRequest request, VentoyInstallJobResult result)
    {
        ThrowIfCancelled(request);

        var exe = FindVentoy2Disk()
                  ?? throw new FileNotFoundException(
                      "Ventoy2Disk.exe 를 찾을 수 없습니다. 배포본 Tools\\ventoy\\Ventoy2Disk.exe 가 포함돼 있는지 확인하세요.");

        var cliArgs = new List<string> { "VTOYCLI", request.IsUpdate ? "/U" : "/I", $"/PhyDrive:{request.DiskNumber}" };

        // /U(업데이트)는 기존 파티션 구성을 그대로 쓰므로 파티션 방식/파일시스템 옵션이 의미 없다.
        if (!request.IsUpdate)
        {
            if (request.PartitionScheme == VentoyPartitionScheme.Gpt)
                cliArgs.Add("/GPT");
            if (request.DisableSecureBoot)
                cliArgs.Add("/NoSB");
            if (request.NonDestructive)
                cliArgs.Add("/NonDest");
            if (request.ReserveSpaceMB > 0)
                cliArgs.Add($"/R:{request.ReserveSpaceMB}");

            var fsName = request.FileSystem switch
            {
                VentoyFileSystem.Ntfs => "NTFS",
                VentoyFileSystem.Fat32 => "FAT32",
                VentoyFileSystem.Udf => "UDF",
                _ => null // exFAT 가 VTOYCLI 기본값이라 굳이 안 넘긴다
            };
            if (fsName is not null)
                cliArgs.Add($"/FS:{fsName}");
        }

        Progress(request, 10, request.IsUpdate ? "Ventoy 업데이트 중..." : "Ventoy 설치 중...");
        RunProcess(exe, cliArgs, request);

        ThrowIfCancelled(request);
        Progress(request, 90, "데이터 파티션 확인 중...");
        result.DataDriveLetter = ResolveDataDriveLetter(request);

        ThrowIfCancelled(request);
        Progress(request, 95, "부팅 메뉴 테마 적용 중...");
        ApplyBranding(result.DataDriveLetter);

        Progress(request, 100, "완료");
    }

    /// <summary>
    /// Ventoy2Disk.exe 는 파티션·부트로더 설치만 하고 테마는 안 건드린다 — 번들해 둔
    /// WinCustoms 테마(Tools\ventoy\plugin\ventoy\theme\, 기본 Ventoy 테마의 배경 이미지만
    /// 우리 로고로 교체한 것)를 데이터 파티션의 /ventoy/ 폴더에 직접 복사해 넣는다.
    /// 실패해도 설치 자체는 이미 끝난 상태라 전체 작업을 실패시키지 않는다(로그만 남김).
    /// </summary>
    private static void ApplyBranding(string? dataDriveLetter)
    {
        if (string.IsNullOrWhiteSpace(dataDriveLetter))
            return;

        try
        {
            var srcThemeDir = Path.Combine(AppContext.BaseDirectory, "Tools", "ventoy", "plugin", "ventoy", "theme");
            if (!Directory.Exists(srcThemeDir))
                return;

            var destVentoyDir = Path.Combine(dataDriveLetter + ":\\", "ventoy");
            var destThemeDir = Path.Combine(destVentoyDir, "theme");
            Directory.CreateDirectory(destThemeDir);

            CopyDirectory(srcThemeDir, destThemeDir);

            var ventoyJsonPath = Path.Combine(destVentoyDir, "ventoy.json");
            File.WriteAllText(ventoyJsonPath, """
                {
                    "theme": {
                        "file": "/ventoy/theme/theme.txt",
                        "display_mode": "GUI"
                    }
                }
                """);
        }
        catch
        {
            // 테마 적용 실패는 치명적이지 않다 — Ventoy 자체는 이미 정상 설치됨.
        }
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);

        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
    }

    /// <summary>USB 자체를 굽는 시점에만 쓰는 배포 폴더 상대 경로. Tools\oscdimg 와 같은 규칙.</summary>
    public static string? FindVentoy2Disk()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Tools", "ventoy", "Ventoy2Disk.exe")
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c))
                return c;
        }

        return null;
    }

    /// <summary>
    /// 설치 직후 데이터 파티션(exFAT/NTFS/FAT32, VTOYEFI 아닌 쪽)에 드라이브 문자를 배정하고 반환한다.
    /// </summary>
    private static string ResolveDataDriveLetter(VentoyInstallJobRequest request)
    {
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $n = {{request.DiskNumber}}
            $part = Get-Partition -DiskNumber $n | Where-Object { $_.Type -ne 'Reserved' } |
                Sort-Object Size -Descending | Select-Object -First 1
            if ($null -eq $part) { throw '데이터 파티션을 찾을 수 없습니다.' }
            if ([string]::IsNullOrEmpty($part.DriveLetter)) {
                $used = (Get-Volume | Where-Object DriveLetter | Select-Object -ExpandProperty DriveLetter)
                $letter = 68..90 | ForEach-Object { [char]$_ } | Where-Object { $used -notcontains $_ } | Select-Object -First 1
                if ($null -eq $letter) { throw '빈 드라이브 문자가 없습니다.' }
                Set-Partition -DiskNumber $n -PartitionNumber $part.PartitionNumber -NewDriveLetter $letter
                Write-Output ([string]$letter)
            } else {
                Write-Output ([string]$part.DriveLetter)
            }
            """;

        return RunPowerShellCapture(script, timeoutMs: 30_000, request: request).Trim();
    }

    /// <summary>
    /// USB/외장 디스크 목록 + Ventoy 설치 여부(볼륨 라벨 기준). 읽기 전용이라 승격 없이도 호출 가능.
    /// </summary>
    public static IReadOnlyList<VentoyDiskInfo> ListRemovableDisks()
    {
        var script = """
            Get-Disk | Where-Object {
              -not $_.IsSystem -and -not $_.IsBoot -and
              ($_.BusType -eq 'USB' -or $_.BusType -eq 'SD' -or $_.BusType -eq 'File Backed Virtual')
            } | ForEach-Object {
              $size = [int64]$_.Size
              $ventoyVer = ''
              try {
                $vol = Get-Partition -DiskNumber $_.Number -ErrorAction SilentlyContinue |
                  Get-Volume -ErrorAction SilentlyContinue |
                  Where-Object { $_.FileSystemLabel -eq 'VTOYEFI' } | Select-Object -First 1
                if ($null -ne $vol) { $ventoyVer = 'installed' }
              } catch {}
              '{0}|{1}|{2}|{3}|{4}|{5}' -f $_.Number, ($_.FriendlyName -replace '\|','/'), $size, $_.BusType, $_.PartitionStyle, $ventoyVer
            }
            """;

        var raw = RunPowerShellCapture(script, timeoutMs: 60_000);
        var list = new List<VentoyDiskInfo>();
        if (string.IsNullOrWhiteSpace(raw)) return list;

        foreach (var line in raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|');
            if (parts.Length < 6) continue;
            if (!int.TryParse(parts[0], out var number)) continue;
            if (!long.TryParse(parts[2], out var size)) continue;

            list.Add(new VentoyDiskInfo
            {
                Number = number,
                FriendlyName = parts[1],
                SizeBytes = size,
                BusType = parts[3],
                PartitionStyle = parts[4],
                VentoyVersion = parts[5] == "installed" ? "?" : null
            });
        }

        return list;
    }

    private static void RunProcess(string file, IReadOnlyList<string> args, VentoyInstallJobRequest request)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        ConsoleEncoding.ApplyTo(psi);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException(file + " 실행 실패");
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();

        var startedUtc = DateTime.UtcNow;
        const int timeoutMs = 15 * 60 * 1000; // Ventoy 설치는 보통 수십 초, 넉넉히 15분
        while (!p.WaitForExit(1500))
        {
            if (!string.IsNullOrWhiteSpace(request.CancelFile) && File.Exists(request.CancelFile))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* */ }
                throw new OperationCanceledException();
            }

            if ((DateTime.UtcNow - startedUtc).TotalMilliseconds >= timeoutMs)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* */ }
                throw new TimeoutException("Ventoy 설치가 시간 초과되었습니다.");
            }
        }

        var stdout = ConsoleEncoding.DecodeAuto(stdoutTask.GetAwaiter().GetResult() ?? string.Empty);
        var stderr = ConsoleEncoding.DecodeAuto(stderrTask.GetAwaiter().GetResult() ?? string.Empty);
        if (p.ExitCode != 0)
            throw new InvalidOperationException(
                $"Ventoy2Disk.exe 종료 코드 {p.ExitCode}. {(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr)}".Trim());
    }

    private static string RunPowerShellCapture(string script, int timeoutMs = 600_000, VentoyInstallJobRequest? request = null)
    {
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        ConsoleEncoding.ApplyTo(psi);
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(encoded);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("PowerShell 실행 실패");
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();

        var startedUtc = DateTime.UtcNow;
        while (!p.WaitForExit(500))
        {
            if (request is not null && !string.IsNullOrWhiteSpace(request.CancelFile) && File.Exists(request.CancelFile))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* */ }
                throw new OperationCanceledException();
            }

            if ((DateTime.UtcNow - startedUtc).TotalMilliseconds >= timeoutMs)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* */ }
                throw new TimeoutException("작업이 시간 초과되었습니다.");
            }
        }

        var stdout = ConsoleEncoding.DecodeAuto(stdoutTask.GetAwaiter().GetResult() ?? string.Empty);
        var stderr = ConsoleEncoding.DecodeAuto(stderrTask.GetAwaiter().GetResult() ?? string.Empty);
        if (p.ExitCode != 0)
            throw new InvalidOperationException(
                "디스크 조회/설정 실패: " + (string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim());

        return stdout;
    }

    private static void Progress(VentoyInstallJobRequest request, int? percent, string message)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.ProgressFile)) return;
            var line = new SystemImageProgressLine { Percent = percent, Message = message };
            File.AppendAllText(
                request.ProgressFile,
                JsonSerializer.Serialize(line, WinCustomsJsonContext.Default.SystemImageProgressLine) + Environment.NewLine,
                System.Text.Encoding.UTF8);
        }
        catch
        {
            // ignore
        }
    }

    private static void ThrowIfCancelled(VentoyInstallJobRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.CancelFile) && File.Exists(request.CancelFile))
            throw new OperationCanceledException();
    }
}
