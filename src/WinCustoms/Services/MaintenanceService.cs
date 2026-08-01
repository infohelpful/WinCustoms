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
        var active = _registry.ReadString(
            RegistryRoot.LocalMachine, RegistryPaths.PowerSchemesKey, "ActivePowerScheme");

        return string.Equals(active, RegistryPaths.UltimatePerformanceGuid, StringComparison.OrdinalIgnoreCase);
    }

    public Task EnableUltimatePerformanceAsync(CancellationToken ct = default)
    {
        var job = new ElevatedJob
        {
            Commands =
            {
                // 이미 만들어져 있으면 오류를 반환하므로 종료 코드를 무시한다.
                new CommandOperation
                {
                    FileName = "powercfg.exe",
                    Arguments = ["-duplicatescheme", RegistryPaths.UltimatePerformanceGuid],
                    IgnoreExitCode = true
                },
                CommandOperation.Create("powercfg.exe", "-setactive", RegistryPaths.UltimatePerformanceGuid)
            }
        };

        return RunJobAsync(job, ct);
    }

    public Task DisableUltimatePerformanceAsync(CancellationToken ct = default)
    {
        var job = new ElevatedJob
        {
            Commands =
            {
                CommandOperation.Create("powercfg.exe", "-setactive", RegistryPaths.BalancedGuid),
                new CommandOperation
                {
                    FileName = "powercfg.exe",
                    Arguments = ["-delete", RegistryPaths.UltimatePerformanceGuid],
                    IgnoreExitCode = true
                }
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
        var script = $"""
            $ErrorActionPreference = 'Stop'
            $drive = "$env:SystemDrive\"
            Enable-ComputerRestore -Drive $drive
            Checkpoint-Computer -Description '{description.Replace("'", "''")}' -RestorePointType 'MODIFY_SETTINGS'
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
