using System.Diagnostics;
using System.Text;
using Microsoft.Win32;
using WinCustoms.Common;

namespace WinCustoms.Services;

public sealed record CleanupReport(int FilesDeleted, long BytesFreed, int Skipped)
{
    public string BytesFreedText => BytesFreed switch
    {
        < 1024 => $"{BytesFreed} B",
        < 1024 * 1024 => $"{BytesFreed / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{BytesFreed / (1024.0 * 1024):0.#} MB",
        _ => $"{BytesFreed / (1024.0 * 1024 * 1024):0.##} GB"
    };
}

public interface IMaintenanceService
{
    bool IsUltimatePerformanceActive();

    Task EnableUltimatePerformanceAsync(CancellationToken ct = default);

    Task DisableUltimatePerformanceAsync(CancellationToken ct = default);

    Task<CleanupReport> CleanTempFilesAsync(CancellationToken ct = default);

    Task CreateRestorePointAsync(string description, CancellationToken ct = default);
}

public sealed class MaintenanceService(IRegistryService registry, IElevationService elevation) : IMaintenanceService
{
    private readonly IRegistryService _registry = registry;
    private readonly IElevationService _elevation = elevation;

    // ── 전원 옵션 ────────────────────────────────────────────────

    public bool IsUltimatePerformanceActive()
    {
        // duplicatescheme 로 만든 플랜은 템플릿 GUID(e9a42b02-…)와 다르다.
        // 활성 구성표 이름에 "최고의 성능" / Ultimate 이 있는지로 판별한다.
        try
        {
            var output = RunPowerCfgCapture(["/getactivescheme"]);
            return LooksLikeUltimatePerformance(output);
        }
        catch
        {
            var active = _registry.ReadString(
                RegistryRoot.LocalMachine, RegistryPaths.PowerSchemesKey, "ActivePowerScheme");
            return string.Equals(active, RegistryPaths.UltimatePerformanceGuid, StringComparison.OrdinalIgnoreCase);
        }
    }

    public Task EnableUltimatePerformanceAsync(CancellationToken ct = default)
    {
        // -duplicatescheme 은 새 GUID 를 만들고, 템플릿 GUID 로는 -setactive 할 수 없다.
        var template = RegistryPaths.UltimatePerformanceGuid;
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $template = '{{template}}'
            $raw = & powercfg.exe -duplicatescheme $template 2>&1 | Out-String
            $guid = $null
            $m = [regex]::Match($raw, '[0-9A-Fa-f]{8}(?:-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}')
            if ($m.Success -and $m.Value -ne $template) { $guid = $m.Value }
            if (-not $guid) {
              $list = & powercfg.exe /L | Out-String
              foreach ($line in ($list -split "`r?`n")) {
                if ($line -notmatch 'Ultimate Performance|최고의 성능|최고 성능') { continue }
                $gm = [regex]::Match($line, '[0-9A-Fa-f]{8}(?:-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}')
                if ($gm.Success -and $gm.Value -ne $template) { $guid = $gm.Value; break }
              }
            }
            if (-not $guid) { throw "최고의 성능 전원 플랜을 만들거나 찾지 못했습니다.`n$raw" }
            & powercfg.exe -setactive $guid
            if ($LASTEXITCODE -ne 0) { throw "powercfg -setactive 실패 (코드 $LASTEXITCODE)" }
            """;

        return RunElevatedPowerShellAsync(script, ct);
    }

    public Task DisableUltimatePerformanceAsync(CancellationToken ct = default)
    {
        var balanced = RegistryPaths.BalancedGuid;
        var template = RegistryPaths.UltimatePerformanceGuid;
        var script = $$"""
            $ErrorActionPreference = 'Continue'
            $balanced = '{{balanced}}'
            $template = '{{template}}'
            & powercfg.exe -setactive $balanced
            if ($LASTEXITCODE -ne 0) { throw "균형 조정 전원 플랜으로 전환하지 못했습니다 (코드 $LASTEXITCODE)" }
            $list = & powercfg.exe /L | Out-String
            foreach ($line in ($list -split "`r?`n")) {
              if ($line -notmatch 'Ultimate Performance|최고의 성능|최고 성능') { continue }
              $gm = [regex]::Match($line, '[0-9A-Fa-f]{8}(?:-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}')
              if (-not $gm.Success) { continue }
              $g = $gm.Value
              if ($g -eq $template -or $g -eq $balanced) { continue }
              & powercfg.exe -delete $g | Out-Null
            }
            """;

        return RunElevatedPowerShellAsync(script, ct);
    }

    private static bool LooksLikeUltimatePerformance(string text)
        => text.Contains("Ultimate Performance", StringComparison.OrdinalIgnoreCase)
           || text.Contains("최고의 성능", StringComparison.Ordinal)
           || text.Contains("최고 성능", StringComparison.Ordinal);

    private static string RunPowerCfgCapture(IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powercfg.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        ConsoleEncoding.ApplyTo(psi);
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("powercfg 실행 실패");
        var stdout = ConsoleEncoding.DecodeAuto(p.StandardOutput.ReadToEnd());
        var stderr = ConsoleEncoding.DecodeAuto(p.StandardError.ReadToEnd());
        p.WaitForExit(30_000);
        return string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
    }

    private Task RunElevatedPowerShellAsync(string script, CancellationToken ct)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var powershell = Path.Combine(system32, "WindowsPowerShell", "v1.0", "powershell.exe");

        var job = new ElevatedJob
        {
            Commands =
            {
                CommandOperation.Create(powershell,
                    "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded)
            }
        };

        return RunJobAsync(job, ct);
    }

    // ── 임시 파일 정리 ───────────────────────────────────────────

    public async Task<CleanupReport> CleanTempFilesAsync(CancellationToken ct = default)
    {
        var files = 0;
        var bytes = 0L;
        var skipped = 0;

        var userTargets = new[]
        {
            Path.GetTempPath(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\Windows\INetCache"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"CrashDumps")
        };

        foreach (var target in userTargets)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(target)) continue;

            var (f, b, s) = PurgeDirectory(target, ct);
            files += f;
            bytes += b;
            skipped += s;
        }

        // C:\Windows\Temp 는 관리자 권한이 필요하다.
        var windowsTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
        if (Directory.Exists(windowsTemp))
        {
            if (_elevation.IsElevated)
            {
                var (f, b, s) = PurgeDirectory(windowsTemp, ct);
                files += f;
                bytes += b;
                skipped += s;
            }
            else
            {
                var job = new ElevatedJob
                {
                    Commands =
                    {
                        new CommandOperation
                        {
                            FileName = "cmd.exe",
                            Arguments = ["/c", $@"del /f /s /q ""{windowsTemp}\*.*"" & rd /s /q ""{windowsTemp}"" & md ""{windowsTemp}"""],
                            IgnoreExitCode = true
                        }
                    }
                };

                await RunJobAsync(job, ct).ConfigureAwait(false);
            }
        }

        return new CleanupReport(files, bytes, skipped);
    }

    private static (int Files, long Bytes, int Skipped) PurgeDirectory(string root, CancellationToken ct)
    {
        var files = 0;
        var bytes = 0L;
        var skipped = 0;

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        foreach (var file in Directory.EnumerateFiles(root, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(file);
                var size = info.Length;
                info.Attributes = FileAttributes.Normal;
                info.Delete();
                files++;
                bytes += size;
            }
            catch
            {
                // 사용 중인 파일은 건너뛴다. 정상적인 상황이다.
                skipped++;
            }
        }

        foreach (var dir in Directory.EnumerateDirectories(root, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var (f, b, s) = PurgeDirectory(dir, ct);
                files += f;
                bytes += b;
                skipped += s;
                Directory.Delete(dir, recursive: false);
            }
            catch
            {
                skipped++;
            }
        }

        return (files, bytes, skipped);
    }

    // ── 시스템 복원 지점 ─────────────────────────────────────────

    public Task CreateRestorePointAsync(string description, CancellationToken ct = default)
    {
        // Checkpoint-Computer 는 일부 PC 에서 VSS/WMI 대기에 걸려 끝없이 기다린다.
        // SystemRestore.CreateRestorePoint + 상위 프로세스 타임아웃으로 처리한다.
        // EventType/RestorePointType 은 uint32 여야 한다(Int32 해시테이블이면 0x80041005).
        var desc = description.Replace("'", "''").Replace("`", "``");
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $desc = '{{desc}}'
            try {
              Enable-ComputerRestore -Drive "$env:SystemDrive\" -ErrorAction SilentlyContinue
            } catch { }

            $code = $null
            try {
              $r = Invoke-CimMethod -Namespace 'root/default' -ClassName 'SystemRestore' -MethodName 'CreateRestorePoint' -Arguments @{
                Description      = [string]$desc
                RestorePointType = [uint32]12
                EventType        = [uint32]100
              }
              if ($null -eq $r) { throw 'CreateRestorePoint 응답이 없습니다.' }
              $code = [int]$r.ReturnValue
            } catch {
              # CIM 타입 이슈/환경 차이가 있으면 구형 WMI 경로로 재시도
              $wmi = [WMIClass]'root\default:SystemRestore'
              $r2 = $wmi.CreateRestorePoint($desc, 12, 100)
              if ($null -eq $r2) { throw $_.Exception.Message }
              $code = [int]$r2.ReturnValue
            }

            if ($code -ne 0) {
              throw "복원 지점 생성 실패 (코드 $code). 시스템 복원이 꺼져 있거나 VSS 서비스에 문제가 있을 수 있습니다."
            }
            """;

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var powershell = Path.Combine(system32, "WindowsPowerShell", "v1.0", "powershell.exe");

        var job = new ElevatedJob
        {
            RegistryOperations =
            {
                // 기본값은 1440분(24시간)에 한 번이라 연속 생성이 조용히 실패한다. 제한을 해제한다.
                RegistryOperation.Set(
                    RegistryRoot.LocalMachine, RegistryPaths.SystemRestore,
                    "SystemRestorePointCreationFrequency", RegistryValueKind.DWord, 0)
            },
            Commands =
            {
                CommandOperation.Create(powershell,
                    "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded)
            }
        };

        return RunJobAsync(job, ct);
    }

    private async Task RunJobAsync(ElevatedJob job, CancellationToken ct)
    {
        var result = await _elevation.RunAsync(job, ct).ConfigureAwait(false);
        if (!result.Success)
            throw new TweakOperationException(string.Join(Environment.NewLine, result.Errors));
    }
}
