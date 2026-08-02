using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using WinCustoms.Common;
using WinCustoms.Models;

namespace WinCustoms.Services;

public sealed record CustomIsoBuildResult(bool Success, string? OutputIsoPath, string? Error);

public interface ICustomIsoService
{
    string? FindOscdimgPath();

    /// <summary>ISO 안의 install.wim/esd 인덱스 목록. (ISO를 임시 마운트하거나 sources만 읽을 수 있으면 사용)</summary>
    Task<IReadOnlyList<WindowsImageInfo>> ListEditionsAsync(string isoPath, CancellationToken ct = default);

    Task<CustomIsoBuildResult> BuildAsync(
        string sourceIso,
        string outputIso,
        int imageIndex,
        IReadOnlyList<TweakItem> tweaks,
        IReadOnlyList<string> appxPackageNames,
        bool bypassSetupRequirements,
        bool injectHostDrivers,
        IProgress<SystemImageProgressLine>? progress,
        CancellationToken ct = default);
}

public sealed class CustomIsoService(IElevationService elevation) : ICustomIsoService
{
    private readonly IElevationService _elevation = elevation;

    public string? FindOscdimgPath() => CustomIsoJobHost.FindOscdimg();

    public async Task<IReadOnlyList<WindowsImageInfo>> ListEditionsAsync(string isoPath, CancellationToken ct = default)
    {
        if (!File.Exists(isoPath))
            throw new FileNotFoundException("ISO 파일을 찾을 수 없습니다.", isoPath);

        // 관리자 없이 ISO 마운트가 막히는 환경이 있어, 승격 PowerShell로 임시 마운트 후 Get-ImageInfo
        var work = Path.Combine(Path.GetTempPath(), "WinCustoms", "iso-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        try
        {
            var script = $$"""
                $ErrorActionPreference = 'Stop'
                $iso = '{{isoPath.Replace("'", "''")}}'
                $work = '{{work.Replace("'", "''")}}'
                $img = Mount-DiskImage -ImagePath $iso -PassThru
                try {
                  $letter = ($img | Get-Volume).DriveLetter
                  $wim = Join-Path ($letter.ToString() + ':\sources') 'install.wim'
                  $esd = Join-Path ($letter.ToString() + ':\sources') 'install.esd'
                  $target = if (Test-Path $wim) { $wim } elseif (Test-Path $esd) { $esd } else { throw 'install.wim/esd 없음' }
                  Copy-Item -LiteralPath $target -Destination (Join-Path $work 'probe.img') -Force
                  Write-Output (Join-Path $work 'probe.img')
                }
                finally {
                  Dismount-DiskImage -ImagePath $iso | Out-Null
                }
                """;

            // 비승격에서도 Mount-DiskImage 가 되는 경우가 많음
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "powershell.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (!File.Exists(psi.FileName)) psi.FileName = "powershell.exe";

            var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-EncodedCommand");
            psi.ArgumentList.Add(encoded);

            using var p = Process.Start(psi) ?? throw new InvalidOperationException("PowerShell 실행 실패");
            var stdout = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            var stderr = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);

            if (p.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);

            var probe = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault();
            if (string.IsNullOrWhiteSpace(probe) || !File.Exists(probe))
                throw new InvalidOperationException("설치 이미지를 ISO에서 읽지 못했습니다.");

            return CustomIsoJobHost.GetImageInfos(probe);
        }
        finally
        {
            try
            {
                if (Directory.Exists(work))
                    Directory.Delete(work, recursive: true);
            }
            catch
            {
                // ignore
            }
        }
    }

    public async Task<CustomIsoBuildResult> BuildAsync(
        string sourceIso,
        string outputIso,
        int imageIndex,
        IReadOnlyList<TweakItem> tweaks,
        IReadOnlyList<string> appxPackageNames,
        bool bypassSetupRequirements,
        bool injectHostDrivers,
        IProgress<SystemImageProgressLine>? progress,
        CancellationToken ct = default)
    {
        var ops = tweaks
            .Where(t => t.SupportsOfflineImage)
            .SelectMany(t => t.OfflineApplyOperations!)
            .ToList();

        var work = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinCustoms", "IsoBuild", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        var request = new CustomIsoJobRequest
        {
            SourceIsoPath = Path.GetFullPath(sourceIso),
            OutputIsoPath = Path.GetFullPath(outputIso),
            ImageIndex = imageIndex <= 0 ? 1 : imageIndex,
            WorkDirectory = work,
            RegistryOperations = ops,
            AppxPackageNames = appxPackageNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            BypassSetupRequirements = bypassSetupRequirements,
            InjectHostDrivers = injectHostDrivers
        };

        return await RunElevatedAsync(request, progress, ct).ConfigureAwait(false);
    }

    private async Task<CustomIsoBuildResult> RunElevatedAsync(
        CustomIsoJobRequest request,
        IProgress<SystemImageProgressLine>? progress,
        CancellationToken ct)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "WinCustoms");
        Directory.CreateDirectory(workDir);

        var id = Guid.NewGuid().ToString("N");
        var jobPath = Path.Combine(workDir, $"customiso-{id}.json");
        var progressPath = jobPath + ".progress";
        var resultPath = jobPath + ".result";
        var cancelPath = jobPath + ".cancel";

        request.ProgressFile = progressPath;
        request.ResultFile = resultPath;
        request.CancelFile = cancelPath;

        await File.WriteAllTextAsync(
            jobPath,
            JsonSerializer.Serialize(request, WinCustomsJsonContext.Default.CustomIsoJobRequest),
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
                        CustomIsoJobHost.Run(["WinCustoms", CustomIsoJobHost.JobSwitch, jobPath]), ct)
                        .ConfigureAwait(false);
                    return await ReadResultAsync(resultPath, request.OutputIsoPath, code, ct).ConfigureAwait(false);
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
                psi.ArgumentList.Add(CustomIsoJobHost.JobSwitch);
                psi.ArgumentList.Add(jobPath);

                process = Process.Start(psi)
                          ?? throw new InvalidOperationException("승격 프로세스를 시작하지 못했습니다.");

                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                return await ReadResultAsync(resultPath, request.OutputIsoPath, process.ExitCode, ct).ConfigureAwait(false);
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

    private static async Task<CustomIsoBuildResult> ReadResultAsync(
        string resultPath, string outputIso, int exitCode, CancellationToken ct)
    {
        if (File.Exists(resultPath))
        {
            var json = await File.ReadAllTextAsync(resultPath, ct).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize(json, WinCustomsJsonContext.Default.CustomIsoJobResult);
            if (parsed is not null)
            {
                return new CustomIsoBuildResult(
                    parsed.Success,
                    parsed.OutputIsoPath ?? outputIso,
                    parsed.Success ? null : (parsed.Error ?? "빌드에 실패했습니다."));
            }
        }

        return exitCode == 0
            ? new CustomIsoBuildResult(true, outputIso, null)
            : new CustomIsoBuildResult(false, null, $"작업이 코드 {exitCode} 로 종료되었습니다.");
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
