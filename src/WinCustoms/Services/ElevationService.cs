using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using WinCustoms.Common;

namespace WinCustoms.Services;

public interface IElevationService
{
    bool IsElevated { get; }

    /// <summary>작업 묶음을 관리자 권한으로 실행한다. 이미 승격 상태면 인프로세스로 처리한다.</summary>
    Task<ElevatedJobResult> RunAsync(ElevatedJob job, CancellationToken ct = default);
}

/// <summary>
/// 관리자 권한이 필요한 작업을 실행한다.
/// 앱은 requireAdministrator 로 기동하므로 보통 인프로세스 처리되고,
/// 비승격 상태(구버전/직접 우회)일 때만 runas 로 재실행한다.
/// </summary>
public sealed class ElevationService : IElevationService
{
    private static readonly Lazy<bool> ElevatedCache = new(DetectElevated);

    public bool IsElevated => ElevatedCache.Value;

    private static bool DetectElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public async Task<ElevatedJobResult> RunAsync(ElevatedJob job, CancellationToken ct = default)
    {
        if (job.IsEmpty)
            return new ElevatedJobResult { Success = true };

        // HKCU 기록을 승격 프로세스에서 올바른 사용자 하이브로 보내기 위함.
        job.TargetUserSid ??= GetCurrentUserSid();

        if (IsElevated)
        {
            // 앱이 requireAdministrator 로 뜨면 항상 이 분기를 탄다. Execute 는 레지스트리
            // 연산뿐 아니라 외부 프로세스(powercfg 등)도 동기로 실행하므로, 여기서 await
            // 없이 그대로 호출하면 호출자(대개 UI 스레드)가 완료까지 통째로 멈춘다 —
            // 창이 "응답 없음" 상태가 되고, 그 상태에서 다시 클릭하면 Windows 가 강제 종료한다.
            var inProcess = new ElevatedJobResult();
            await Task.Run(() => ElevatedJobHost.Execute(job, inProcess, ct), ct).ConfigureAwait(false);
            inProcess.Success = inProcess.Errors.Count == 0;
            return inProcess;
        }

        var workDir = Path.Combine(Path.GetTempPath(), "WinCustoms");
        Directory.CreateDirectory(workDir);

        var jobPath = Path.Combine(workDir, $"job-{Guid.NewGuid():N}.json");
        var resultPath = jobPath + ".result";

        try
        {
            var payload = JsonSerializer.Serialize(job, WinCustomsJsonContext.Default.ElevatedJob);
            await File.WriteAllTextAsync(jobPath, payload, ct).ConfigureAwait(false);

            var exePath = Environment.ProcessPath
                          ?? throw new InvalidOperationException("실행 파일 경로를 확인할 수 없습니다.");

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,   // runas 동사를 쓰려면 필수
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            psi.ArgumentList.Add(ElevatedJobHost.JobSwitch);
            psi.ArgumentList.Add(jobPath);

            using var process = Process.Start(psi)
                                ?? throw new InvalidOperationException("승격 프로세스를 시작하지 못했습니다.");

            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            if (File.Exists(resultPath))
            {
                var json = await File.ReadAllTextAsync(resultPath, ct).ConfigureAwait(false);
                var parsed = JsonSerializer.Deserialize(json, WinCustomsJsonContext.Default.ElevatedJobResult);
                if (parsed is not null) return parsed;
            }

            return process.ExitCode == 0
                ? new ElevatedJobResult { Success = true }
                : new ElevatedJobResult { Success = false, Errors = { $"승격 작업이 코드 {process.ExitCode} 로 종료되었습니다." } };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            throw new ElevationDeniedException();
        }
        finally
        {
            TryDelete(jobPath);
            TryDelete(resultPath);
        }
    }

    private static string? GetCurrentUserSid()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value;
        }
        catch
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 임시 파일 정리 실패는 무시한다.
        }
    }
}
