using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using WinCustoms.Common;
using WinCustoms.Models;

namespace WinCustoms.Services;

public sealed record BootUsbBuildResult(bool Success, string? TargetDescription, string? Error);

public interface IBootUsbService
{
    Task<IReadOnlyList<BootUsbDiskInfo>> ListDisksAsync(CancellationToken ct = default);

    Task<IReadOnlyList<WindowsImageInfo>> ListEditionsAsync(string isoPath, CancellationToken ct = default);

    Task<BootUsbBuildResult> CreateAsync(
        BootUsbJobRequest requestTemplate,
        IReadOnlyList<TweakItem> tweaks,
        IReadOnlyList<string> appxPackageNames,
        IProgress<SystemImageProgressLine>? progress,
        CancellationToken ct = default);
}

public sealed class BootUsbService(ICustomIsoService iso, IElevationService elevation) : IBootUsbService
{
    private readonly ICustomIsoService _iso = iso;
    private readonly IElevationService _elevation = elevation;

    public Task<IReadOnlyList<BootUsbDiskInfo>> ListDisksAsync(CancellationToken ct = default)
        => Task.Run(() => (IReadOnlyList<BootUsbDiskInfo>)BootUsbJobHost.ListRemovableDisks(), ct);

    public Task<IReadOnlyList<WindowsImageInfo>> ListEditionsAsync(string isoPath, CancellationToken ct = default)
        => _iso.ListEditionsAsync(isoPath, ct);

    public async Task<BootUsbBuildResult> CreateAsync(
        BootUsbJobRequest requestTemplate,
        IReadOnlyList<TweakItem> tweaks,
        IReadOnlyList<string> appxPackageNames,
        IProgress<SystemImageProgressLine>? progress,
        CancellationToken ct = default)
    {
        var ops = tweaks
            .Where(t => t.SupportsOfflineImage)
            .SelectMany(t => t.OfflineApplyOperations!)
            .ToList();

        var work = WinCustomsWorkCleanup.CreateJobWorkDirectory("BootUsb");

        try
        {
            var request = new BootUsbJobRequest
            {
                SourceIsoPath = Path.GetFullPath(requestTemplate.SourceIsoPath),
                ImageIndex = requestTemplate.ImageIndex <= 0 ? 1 : requestTemplate.ImageIndex,
                EditionName = requestTemplate.EditionName ?? string.Empty,
                WorkDirectory = work,
                DiskNumber = requestTemplate.DiskNumber,
                DiskFriendlyName = requestTemplate.DiskFriendlyName,
                DiskSizeBytes = requestTemplate.DiskSizeBytes,
                PartitionScheme = requestTemplate.PartitionScheme,
                FileSystem = requestTemplate.FileSystem,
                VolumeLabel = requestTemplate.VolumeLabel,
                ClusterSizeBytes = requestTemplate.ClusterSizeBytes,
                QuickFormat = requestTemplate.QuickFormat,
                CreateExtendedLabelAndIcon = requestTemplate.CreateExtendedLabelAndIcon,
                RegistryOperations = ops,
                AppxPackageNames = appxPackageNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                BypassSetupRequirements = requestTemplate.BypassSetupRequirements,
                InjectHostDrivers = requestTemplate.InjectHostDrivers,
                SkipOnlineAccount = requestTemplate.SkipOnlineAccount,
                SkipPrivacyExperience = requestTemplate.SkipPrivacyExperience,
                LocalAccountName = (requestTemplate.LocalAccountName ?? string.Empty).Trim(),
                EnableAutoLogon = requestTemplate.EnableAutoLogon,
                LocalAccountPassword = requestTemplate.LocalAccountPassword ?? string.Empty
            };

            return await RunElevatedAsync(request, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            // 승격 프로세스가 죽어도 UI 쪽에서 한 번 더 청소
            WinCustomsWorkCleanup.TryDeleteTree(work);
        }
    }

    private async Task<BootUsbBuildResult> RunElevatedAsync(
        BootUsbJobRequest request,
        IProgress<SystemImageProgressLine>? progress,
        CancellationToken ct)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "WinCustoms");
        Directory.CreateDirectory(workDir);

        var id = Guid.NewGuid().ToString("N");
        var jobPath = Path.Combine(workDir, $"bootusb-{id}.json");
        var progressPath = jobPath + ".progress";
        var resultPath = jobPath + ".result";
        var cancelPath = jobPath + ".cancel";

        request.ProgressFile = progressPath;
        request.ResultFile = resultPath;
        request.CancelFile = cancelPath;

        await File.WriteAllTextAsync(
            jobPath,
            JsonSerializer.Serialize(request, WinCustomsJsonContext.Default.BootUsbJobRequest),
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
                        BootUsbJobHost.Run(["WinCustoms", BootUsbJobHost.JobSwitch, jobPath]), ct)
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
                psi.ArgumentList.Add(BootUsbJobHost.JobSwitch);
                psi.ArgumentList.Add(jobPath);

                process = Process.Start(psi)
                          ?? throw new InvalidOperationException("승격 프로세스를 시작하지 못했습니다.");

                await process.WaitForExitAsync(ct).ConfigureAwait(false);
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

    private static async Task<BootUsbBuildResult> ReadResultAsync(string resultPath, int exitCode, CancellationToken ct)
    {
        if (File.Exists(resultPath))
        {
            var json = await File.ReadAllTextAsync(resultPath, ct).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize(json, WinCustomsJsonContext.Default.BootUsbJobResult);
            if (parsed is not null)
            {
                return new BootUsbBuildResult(
                    parsed.Success,
                    parsed.TargetDescription,
                    parsed.Success ? null : (parsed.Error ?? "부팅 USB 작성에 실패했습니다."));
            }
        }

        return exitCode == 0
            ? new BootUsbBuildResult(true, null, null)
            : new BootUsbBuildResult(false, null, $"작업이 코드 {exitCode} 로 종료되었습니다.");
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
