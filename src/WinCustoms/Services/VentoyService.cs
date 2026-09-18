using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using WinCustoms.Common;

namespace WinCustoms.Services;

public sealed record VentoyInstallResult(bool Success, string? DataDriveLetter, string? Error);

public interface IVentoyService
{
    Task<IReadOnlyList<VentoyDiskInfo>> ListDisksAsync(CancellationToken ct = default);

    Task<VentoyInstallResult> InstallAsync(
        VentoyInstallJobRequest requestTemplate,
        IProgress<SystemImageProgressLine>? progress,
        CancellationToken ct = default);
}

/// <summary>
/// Ventoy2Disk.exe(Tools\ventoy\, 공식 배포 바이너리 그대로 동봉) 를 VTOYCLI 명령줄로
/// 실행해 부팅 USB에 Ventoy 를 설치/업데이트한다. BootUsbService 와 같은 승격·진행률·
/// 취소 패턴을 그대로 따른다.
/// </summary>
public sealed class VentoyService(IElevationService elevation) : IVentoyService
{
    private readonly IElevationService _elevation = elevation;

    public Task<IReadOnlyList<VentoyDiskInfo>> ListDisksAsync(CancellationToken ct = default)
        => Task.Run(() => (IReadOnlyList<VentoyDiskInfo>)VentoyJobHost.ListRemovableDisks(), ct);

    public async Task<VentoyInstallResult> InstallAsync(
        VentoyInstallJobRequest requestTemplate,
        IProgress<SystemImageProgressLine>? progress,
        CancellationToken ct = default)
    {
        var request = new VentoyInstallJobRequest
        {
            DiskNumber = requestTemplate.DiskNumber,
            DiskFriendlyName = requestTemplate.DiskFriendlyName,
            DiskSizeBytes = requestTemplate.DiskSizeBytes,
            PartitionScheme = requestTemplate.PartitionScheme,
            FileSystem = requestTemplate.FileSystem,
            IsUpdate = requestTemplate.IsUpdate,
            NonDestructive = requestTemplate.NonDestructive,
            DisableSecureBoot = requestTemplate.DisableSecureBoot,
            ReserveSpaceMB = requestTemplate.ReserveSpaceMB
        };

        return await RunElevatedAsync(request, progress, ct).ConfigureAwait(false);
    }

    private async Task<VentoyInstallResult> RunElevatedAsync(
        VentoyInstallJobRequest request,
        IProgress<SystemImageProgressLine>? progress,
        CancellationToken ct)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "WinCustoms");
        Directory.CreateDirectory(workDir);

        var id = Guid.NewGuid().ToString("N");
        var jobPath = Path.Combine(workDir, $"ventoy-{id}.json");
        var progressPath = jobPath + ".progress";
        var resultPath = jobPath + ".result";
        var cancelPath = jobPath + ".cancel";

        request.ProgressFile = progressPath;
        request.ResultFile = resultPath;
        request.CancelFile = cancelPath;

        await File.WriteAllTextAsync(
            jobPath,
            JsonSerializer.Serialize(request, WinCustomsJsonContext.Default.VentoyInstallJobRequest),
            ct).ConfigureAwait(false);

        await using var cancelReg = ct.Register(() =>
        {
            try { File.WriteAllText(cancelPath, "1"); } catch { /* */ }
        });

        Process? process = null;

        try
        {
            using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var progressPump = PumpProgressAsync(progressPath, progress, progressCts.Token);

            try
            {
                if (_elevation.IsElevated)
                {
                    var code = await Task.Run(() =>
                        VentoyJobHost.Run(["WinCustoms", VentoyJobHost.JobSwitch, jobPath]), ct)
                        .ConfigureAwait(false);
                    return await ReadResultAsync(resultPath, code, ct).ConfigureAwait(false);
                }

                var exePath = Environment.ProcessPath
                              ?? throw new InvalidOperationException("실행 파일 경로를 확인할 수 없습니다.");

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,
                    Verb = "runas",
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                psi.ArgumentList.Add(VentoyJobHost.JobSwitch);
                psi.ArgumentList.Add(jobPath);

                process = Process.Start(psi)
                          ?? throw new InvalidOperationException("승격 프로세스를 시작하지 못했습니다.");

                try
                {
                    await process.WaitForExitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // WaitForExitAsync 취소는 승격 프로세스를 안 죽인다 — cancelPath 는 아래
                    // finally 에서 곧바로 지워지므로, 죽이지 않으면 취소 신호를 영영 못 보고
                    // 디스크 작업이 백그라운드에서 계속 돈다. 실제로도 강제 종료한다.
                    try { process.Kill(entireProcessTree: true); } catch { /* 이미 종료됨 */ }
                    throw;
                }

                return await ReadResultAsync(resultPath, process.ExitCode, ct).ConfigureAwait(false);
            }
            finally
            {
                progressCts.Cancel();
                try { await progressPump.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* */ }
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new ElevationDeniedException();
        }
        finally
        {
            process?.Dispose();
            TryDelete(jobPath);
            TryDelete(progressPath);
            TryDelete(resultPath);
            TryDelete(cancelPath);
        }
    }

    private static async Task<VentoyInstallResult> ReadResultAsync(string resultPath, int exitCode, CancellationToken ct)
    {
        if (File.Exists(resultPath))
        {
            var json = await File.ReadAllTextAsync(resultPath, ct).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize(json, WinCustomsJsonContext.Default.VentoyInstallJobResult);
            if (parsed is not null)
            {
                return new VentoyInstallResult(
                    parsed.Success,
                    parsed.DataDriveLetter,
                    parsed.Success ? null : (parsed.Error ?? "Ventoy 설치에 실패했습니다."));
            }
        }

        return exitCode == 0
            ? new VentoyInstallResult(true, null, null)
            : new VentoyInstallResult(false, null, $"작업이 코드 {exitCode} 로 종료되었습니다.");
    }

    private static async Task PumpProgressAsync(
        string progressPath,
        IProgress<SystemImageProgressLine>? progress,
        CancellationToken ct)
    {
        if (progress is null) return;
        long position = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(progressPath))
                {
                    await using var stream = new FileStream(
                        progressPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (stream.Length > position)
                    {
                        stream.Seek(position, SeekOrigin.Begin);
                        using var reader = new StreamReader(stream);
                        var text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                        position = stream.Position;

                        foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var line = raw.Trim();
                            if (line.Length == 0) continue;
                            try
                            {
                                var parsed = JsonSerializer.Deserialize(
                                    line, WinCustomsJsonContext.Default.SystemImageProgressLine);
                                if (parsed is not null) progress.Report(parsed);
                            }
                            catch
                            {
                                // skip
                            }
                        }
                    }
                }
            }
            catch
            {
                // retry
            }

            try { await Task.Delay(400, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* */ }
    }
}
