using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using WinCustoms.Common;

namespace WinCustoms.Services;

public sealed record SystemVolumeInfo(string Root, string Label, long TotalBytes, long FreeBytes, bool IsSystem)
{
    public string DisplayText
    {
        get
        {
            static string Fmt(long bytes) => bytes switch
            {
                < 1024 => $"{bytes} B",
                < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
                < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
                _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"
            };

            return $"{Root} {Label} — 여유 {Fmt(FreeBytes)} / 전체 {Fmt(TotalBytes)}";
        }
    }
}


public sealed record SystemImageOperationResult(bool Success, string? ImageFile, string? Error);

public interface ISystemImageService
{
    /// <summary>캡처/복원에 쓸 수 있는 고정 디스크 볼륨 목록.</summary>
    IReadOnlyList<SystemVolumeInfo> ListVolumes();

    string GetSystemVolumeRoot();

    /// <summary>현재 Windows 볼륨을 WIM 으로 캡처한다. 관리자(UAC) 필요.</summary>
    Task<SystemImageOperationResult> CaptureAsync(
        string imageFilePath,
        string imageName,
        IProgress<SystemImageProgressLine>? progress,
        CancellationToken ct = default);

    /// <summary>WIM 옆에 C: 복원 스크립트·안내를 (재)생성한다.</summary>
    void WriteRestoreScripts(string imageFilePath, string? imageName = null);

    /// <summary>
    /// WinRE에 자동 복원을 심고 다음 부팅을 복구 환경으로 보낸다.
    /// 재시작 후 플래그가 보이면 명령 프롬프트 없이 C:에 백업을 적용한다.
    /// </summary>
    Task<SystemImageOperationResult> PrepareAutomaticRestoreAsync(
        string imageFilePath,
        IProgress<SystemImageProgressLine>? progress,
        CancellationToken ct = default);

    /// <summary>고급 시작 옵션(WinRE 선택 화면)으로 다시 시작한다.</summary>
    Task RebootToWinREAsync(CancellationToken ct = default);
}

public sealed class SystemImageService(IElevationService elevation) : ISystemImageService
{
    private readonly IElevationService _elevation = elevation;

    public string GetSystemVolumeRoot()
    {
        var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        return string.IsNullOrWhiteSpace(root) ? @"C:\" : root;
    }

    public IReadOnlyList<SystemVolumeInfo> ListVolumes()
    {
        var systemRoot = GetSystemVolumeRoot().TrimEnd('\\') + "\\";
        var list = new List<SystemVolumeInfo>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;

                var root = drive.RootDirectory.FullName;
                list.Add(new SystemVolumeInfo(
                    root,
                    string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.Name.TrimEnd('\\') : drive.VolumeLabel,
                    drive.TotalSize,
                    drive.AvailableFreeSpace,
                    string.Equals(root.TrimEnd('\\'), systemRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)));
            }
            catch
            {
                // 접근 불가 볼륨은 건너뛴다.
            }
        }

        return list;
    }

    public Task<SystemImageOperationResult> CaptureAsync(
        string imageFilePath,
        string imageName,
        IProgress<SystemImageProgressLine>? progress,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(imageFilePath))
            throw new ArgumentException("저장할 WIM 경로가 필요합니다.", nameof(imageFilePath));

        var fullPath = Path.GetFullPath(imageFilePath);
        if (!string.Equals(Path.GetExtension(fullPath), ".wim", StringComparison.OrdinalIgnoreCase))
            fullPath = Path.ChangeExtension(fullPath, ".wim");

        var dir = Path.GetDirectoryName(fullPath)
                  ?? throw new InvalidOperationException("저장 경로가 올바르지 않습니다.");
        Directory.CreateDirectory(dir);

        var systemRoot = GetSystemVolumeRoot();
        WarnIfSameVolume(fullPath, systemRoot);

        var request = new SystemImageJobRequest
        {
            Kind = SystemImageJobKind.Capture,
            ImageFile = fullPath,
            ImageName = string.IsNullOrWhiteSpace(imageName) ? $"WinCustoms {DateTime.Now:yyyy-MM-dd}" : imageName.Trim(),
            CaptureVolume = systemRoot
        };

        return RunElevatedJobAsync(request, progress, ct);
    }

    public void WriteRestoreScripts(string imageFilePath, string? imageName = null)
    {
        if (!File.Exists(imageFilePath))
            throw new FileNotFoundException("WIM 파일을 찾을 수 없습니다.", imageFilePath);

        SystemImageCompanionFiles.Write(
            imageFilePath,
            string.IsNullOrWhiteSpace(imageName) ? Path.GetFileNameWithoutExtension(imageFilePath) : imageName.Trim());
    }

    public Task<SystemImageOperationResult> PrepareAutomaticRestoreAsync(
        string imageFilePath,
        IProgress<SystemImageProgressLine>? progress,
        CancellationToken ct = default)
    {
        if (!File.Exists(imageFilePath))
            throw new FileNotFoundException("WIM 파일을 찾을 수 없습니다.", imageFilePath);

        var request = new SystemImageJobRequest
        {
            Kind = SystemImageJobKind.PrepareAutoRestore,
            ImageFile = Path.GetFullPath(imageFilePath)
        };

        return RunElevatedJobAsync(request, progress, ct);
    }

    public async Task RebootToWinREAsync(CancellationToken ct = default)
    {
        // /r 다시 시작, /o 고급 시작 옵션, /f 앱 강제 종료
        var psi = new ProcessStartInfo
        {
            FileName = "shutdown.exe",
            UseShellExecute = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("/r");
        psi.ArgumentList.Add("/o");
        psi.ArgumentList.Add("/f");
        psi.ArgumentList.Add("/t");
        psi.ArgumentList.Add("0");

        Process.Start(psi)?.Dispose();
        await Task.Delay(500, ct).ConfigureAwait(false);
    }

    private async Task<SystemImageOperationResult> RunElevatedJobAsync(
        SystemImageJobRequest request,
        IProgress<SystemImageProgressLine>? progress,
        CancellationToken ct)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "WinCustoms");
        Directory.CreateDirectory(workDir);

        var id = Guid.NewGuid().ToString("N");
        var jobPath = Path.Combine(workDir, $"sysimg-{id}.json");
        var progressPath = jobPath + ".progress";
        var resultPath = jobPath + ".result";
        var cancelPath = jobPath + ".cancel";

        request.ProgressFile = progressPath;
        request.ResultFile = resultPath;
        request.CancelFile = cancelPath;

        await File.WriteAllTextAsync(
            jobPath,
            JsonSerializer.Serialize(request, WinCustomsJsonContext.Default.SystemImageJobRequest),
            ct).ConfigureAwait(false);

        await using var cancelReg = ct.Register(() =>
        {
            try { File.WriteAllText(cancelPath, "1"); } catch { /* ignore */ }
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
                        SystemImageJobHost.Run(["WinCustoms", SystemImageJobHost.JobSwitch, jobPath]), ct)
                        .ConfigureAwait(false);

                    return await ReadResultAsync(resultPath, request.ImageFile, code, ct).ConfigureAwait(false);
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
                psi.ArgumentList.Add(SystemImageJobHost.JobSwitch);
                psi.ArgumentList.Add(jobPath);

                process = Process.Start(psi)
                          ?? throw new InvalidOperationException("승격 프로세스를 시작하지 못했습니다.");

                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                return await ReadResultAsync(resultPath, request.ImageFile, process.ExitCode, ct).ConfigureAwait(false);
            }
            finally
            {
                progressCts.Cancel();
                try { await progressPump.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* expected */ }
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

    private static async Task<SystemImageOperationResult> ReadResultAsync(
        string resultPath, string imageFile, int exitCode, CancellationToken ct)
    {
        if (File.Exists(resultPath))
        {
            var json = await File.ReadAllTextAsync(resultPath, ct).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize(json, WinCustomsJsonContext.Default.SystemImageJobResult);
            if (parsed is not null)
            {
                return new SystemImageOperationResult(
                    parsed.Success,
                    parsed.ImageFile ?? imageFile,
                    parsed.Success ? null : (parsed.Error ?? "작업에 실패했습니다."));
            }
        }

        return exitCode == 0
            ? new SystemImageOperationResult(true, imageFile, null)
            : new SystemImageOperationResult(false, null, $"작업이 코드 {exitCode} 로 종료되었습니다.");
    }

    private static async Task PumpProgressAsync(
        string progressPath,
        IProgress<SystemImageProgressLine>? progress,
        CancellationToken ct)
    {
        if (progress is null) return;

        long position = 0;
        var buffer = new MemoryStream();

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
                        await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
                        position = stream.Position;

                        var text = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
                        buffer.SetLength(0);

                        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                        // 마지막 줄이 불완전할 수 있으면 다음 루프에서 이어 읽도록 단순화:
                        // AppendAllText 는 줄 단위라 대부분 완전하다.
                        foreach (var raw in lines)
                        {
                            var line = raw.Trim();
                            if (line.Length == 0) continue;
                            try
                            {
                                var parsed = JsonSerializer.Deserialize(
                                    line, WinCustomsJsonContext.Default.SystemImageProgressLine);
                                if (parsed is not null)
                                    progress.Report(parsed);
                            }
                            catch
                            {
                                // 깨진 줄은 무시.
                            }
                        }
                    }
                }
            }
            catch
            {
                // 읽기 경합은 다음 주기에 재시도.
            }

            try
            {
                await Task.Delay(400, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static void WarnIfSameVolume(string imageFile, string systemRoot)
    {
        var imageRoot = Path.GetPathRoot(imageFile);
        if (imageRoot is not null
            && string.Equals(imageRoot.TrimEnd('\\'), systemRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            // 서비스 단에서는 막지 않는다. UI 에서 경고한다.
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
            // 임시 파일 정리 실패는 무시.
        }
    }
}
