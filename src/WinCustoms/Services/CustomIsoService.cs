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

        // DISM /Get-ImageInfo 는 관리자 권한이 필요하다(비승격 시 740).
        // install.wim 전체를 복사하지 않고, ISO를 잠깐 마운트한 뒤 그 경로로 조회한다.
        var work = Path.Combine(Path.GetTempPath(), "WinCustoms", "iso-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var infoFile = Path.Combine(work, "imageinfo.txt");

        try
        {
            var isoEsc = isoPath.Replace("'", "''");
            var outEsc = infoFile.Replace("'", "''");
            var script = $$"""
                $ErrorActionPreference = 'Stop'
                $iso = '{{isoEsc}}'
                $out = '{{outEsc}}'
                if (-not (Test-Path -LiteralPath $iso)) { throw "ISO 없음: $iso" }

                $img = Mount-DiskImage -ImagePath $iso -PassThru
                try {
                  Start-Sleep -Milliseconds 400
                  $vol = Get-DiskImage -ImagePath $iso | Get-Volume
                  if ($null -eq $vol) { throw 'ISO 볼륨을 찾지 못했습니다.' }
                  $letter = [string]$vol.DriveLetter
                  if ([string]::IsNullOrWhiteSpace($letter)) { throw 'ISO 드라이브 문자가 없습니다. 다른 프로그램이 ISO를 사용 중일 수 있습니다.' }

                  $root = $letter + ':\'
                  $wim = Join-Path $root 'sources\install.wim'
                  $esd = Join-Path $root 'sources\install.esd'
                  if (Test-Path -LiteralPath $wim) { $target = $wim }
                  elseif (Test-Path -LiteralPath $esd) { $target = $esd }
                  else { throw 'sources\install.wim / install.esd 를 찾을 수 없습니다. 순정 Windows ISO인지 확인하세요.' }

                  $dism = Join-Path $env:SystemRoot 'System32\dism.exe'
                  $raw = & $dism /Get-ImageInfo "/ImageFile:$target" 2>&1 | Out-String
                  if ($LASTEXITCODE -ne 0) {
                    throw ("DISM 종료 코드 {0}. {1}" -f $LASTEXITCODE, $raw.Trim())
                  }
                  Set-Content -LiteralPath $out -Value $raw -Encoding UTF8
                }
                finally {
                  Dismount-DiskImage -ImagePath $iso -ErrorAction SilentlyContinue | Out-Null
                }
                """;

            await RunElevatedPowerShellAsync(script, ct).ConfigureAwait(false);

            if (!File.Exists(infoFile))
                throw new InvalidOperationException("DISM 이미지 정보 파일이 만들어지지 않았습니다.");

            var text = await File.ReadAllTextAsync(infoFile, ct).ConfigureAwait(false);
            var list = CustomIsoJobHost.ParseImageInfo(text);
            if (list.Count == 0)
            {
                var preview = text.Length <= 600 ? text.Trim() : text.Trim()[..600] + "…";
                throw new InvalidOperationException(
                    "에디션 목록을 해석하지 못했습니다. DISM 출력:\n" + preview);
            }

            return list;
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

    private async Task RunElevatedPowerShellAsync(string script, CancellationToken ct)
    {
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var powershell = Path.Combine(system32, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell)) powershell = "powershell.exe";

        if (_elevation.IsElevated)
        {
            await RunPowerShellLocalAsync(powershell, encoded, ct).ConfigureAwait(false);
            return;
        }

        var job = new ElevatedJob
        {
            Commands =
            {
                CommandOperation.Create(powershell,
                    "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded)
            }
        };

        var result = await _elevation.RunAsync(job, ct).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException(string.Join(Environment.NewLine, result.Errors));
    }

    private static async Task RunPowerShellLocalAsync(string powershell, string encoded, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = powershell,
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
        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (p.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
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
