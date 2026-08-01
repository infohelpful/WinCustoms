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
/// 관리자 권한이 필요한 작업을 "자기 자신을 runas 로 재실행" 방식으로 처리한다.
///
/// 앱 전체를 관리자로 띄우지 않는 이유
///  - 관리자 프로세스에서 HKCU 를 쓰면 (계정에 따라) 다른 사용자 하이브에 기록될 수 있다.
///  - 탐색기 드래그 앤 드롭 등 셸 연동이 UIPI 로 막힌다.
/// 그래서 UI 는 asInvoker 로 두고, HKLM 작업만 짧게 승격시킨다.
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

        if (IsElevated)
        {
            var inProcess = new ElevatedJobResult();
            ElevatedJobHost.Execute(job, inProcess);
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
