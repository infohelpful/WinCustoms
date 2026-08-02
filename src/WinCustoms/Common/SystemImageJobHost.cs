using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinCustoms.Common;

/// <summary>
/// 장시간 시스템 이미지(캡처/복원) 작업을 승격 프로세스에서 수행한다.
/// 진행률은 ProgressFile 에 JSON 줄로 쌓고, UI 프로세스가 이를 읽어 표시한다.
/// </summary>
public static class SystemImageJobHost
{
    public const string JobSwitch = "--system-image-job";

    private static readonly Regex PercentRegex = new(
        @"(\d{1,3}(?:\.\d+)?)\s*%",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

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

        SystemImageJobRequest? request = null;
        var result = new SystemImageJobResult();

        try
        {
            var json = File.ReadAllText(jobPath);
            request = JsonSerializer.Deserialize(json, WinCustomsJsonContext.Default.SystemImageJobRequest)
                      ?? throw new InvalidOperationException("시스템 이미지 작업 파일을 해석할 수 없습니다.");

            WriteProgress(request, null, "작업을 시작합니다...");

            switch (request.Kind)
            {
                case SystemImageJobKind.Capture:
                    Capture(request);
                    break;
                case SystemImageJobKind.Apply:
                    Apply(request);
                    break;
                case SystemImageJobKind.PrepareAutoRestore:
                    SystemImageAutoRestore.Prepare(request, (p, m) => WriteProgress(request, p, m));
                    break;
                default:
                    throw new InvalidOperationException("알 수 없는 시스템 이미지 작업입니다.");
            }

            result.Success = true;
            result.ImageFile = request.ImageFile;
            WriteProgress(request, 100, "완료");
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.Error = "작업이 취소되었습니다.";
            if (request is not null)
                WriteProgress(request, null, result.Error);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            if (request is not null)
                WriteProgress(request, null, "오류: " + ex.Message);
        }

        try
        {
            var resultPath = request?.ResultFile ?? (jobPath + ".result");
            File.WriteAllText(resultPath, JsonSerializer.Serialize(result, WinCustomsJsonContext.Default.SystemImageJobResult));
        }
        catch
        {
            // 결과 파일 기록 실패는 종료 코드로만 전달.
        }

        return result.Success ? 0 : 1;
    }

    private static void Capture(SystemImageJobRequest request)
    {
        ThrowIfCancelled(request);

        var volume = NormalizeVolume(request.CaptureVolume);
        var imageFile = request.ImageFile;
        var directory = Path.GetDirectoryName(imageFile)
                        ?? throw new InvalidOperationException("이미지 저장 경로가 올바르지 않습니다.");

        Directory.CreateDirectory(directory);

        if (File.Exists(imageFile))
            File.Delete(imageFile);

        // 스크래치는 저장 드라이브에 둔다. C: TEMP 를 쓰면 섀도 캡처와 경합·지연이 난다.
        var scratchDir = Path.Combine(directory, ".wincustoms-dism-scratch");
        Directory.CreateDirectory(scratchDir);

        WriteProgress(request, 1, $"볼륨 섀도 복사본 생성 중 ({volume})...");
        var shadowId = CreateShadowCopy(volume);
        string? mountLink = null;

        try
        {
            ThrowIfCancelled(request);
            var devicePath = ResolveShadowDevicePath(shadowId);
            WriteProgress(request, 2, "섀도 복사본을 캡처 경로에 연결하는 중...");
            mountLink = MountShadowLink(devicePath);
            WriteProgress(request, 3, "DISM 캡처 시작 (파일 수가 많으면 처음 수 분은 %가 안 오를 수 있음)...");

            var dism = ResolveDismPath();
            var name = string.IsNullOrWhiteSpace(request.ImageName) ? "WinCustoms Backup" : request.ImageName.Trim();
            var description = $"WinCustoms system backup {DateTime.Now:yyyy-MM-dd HH:mm}";

            // /CheckIntegrity 는 캡처 시간을 크게 늘리고 초반에 멈춘 것처럼 보이게 해서 제외.
            var args = new[]
            {
                "/Capture-Image",
                $"/ImageFile:{imageFile}",
                $"/CaptureDir:{mountLink}",
                $"/Name:{name}",
                $"/Description:{description}",
                "/Compress:fast",
                $"/ScratchDir:{scratchDir}"
            };

            RunDism(dism, args, request, watchFile: imageFile);

            if (!File.Exists(imageFile))
                throw new InvalidOperationException("캡처가 끝났지만 WIM 파일이 만들어지지 않았습니다.");

            WriteCompanionFiles(imageFile, name);
            WriteProgress(request, 99, "복원 안내 파일을 저장했습니다.");
        }
        finally
        {
            TryUnmountShadowLink(mountLink);
            TryDeleteShadowCopy(shadowId);
            TryDeleteDirectory(scratchDir);
        }
    }

    private static void Apply(SystemImageJobRequest request)
    {
        ThrowIfCancelled(request);

        if (string.IsNullOrWhiteSpace(request.ApplyDir))
            throw new InvalidOperationException("복원 대상 경로가 없습니다.");

        var applyDir = NormalizeApplyDir(request.ApplyDir);
        var systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))
                         ?? @"C:\";
        var applyRoot = Path.GetPathRoot(applyDir) ?? applyDir;

        if (string.Equals(systemRoot.TrimEnd('\\'), applyRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("현재 실행 중인 Windows가 있는 드라이브에는 복원할 수 없습니다.");

        if (!File.Exists(request.ImageFile))
            throw new FileNotFoundException("WIM 파일을 찾을 수 없습니다.", request.ImageFile);

        Directory.CreateDirectory(applyDir);

        WriteProgress(request, 2, $"이미지를 {applyDir} 에 적용하는 중...");

        var dism = ResolveDismPath();
        var index = request.ImageIndex <= 0 ? 1 : request.ImageIndex;
        RunDism(dism,
        [
            "/Apply-Image",
            $"/ImageFile:{request.ImageFile}",
            $"/Index:{index}",
            $"/ApplyDir:{applyDir}",
            "/CheckIntegrity"
        ], request);

        var windowsDir = Path.Combine(applyDir, "Windows");
        if (!Directory.Exists(windowsDir))
            throw new InvalidOperationException("적용 후 Windows 폴더를 찾지 못했습니다. 대상 파티션/인덱스를 확인하세요.");

        WriteProgress(request, 92, "부팅 구성(bcdboot)을 갱신하는 중...");
        TryRunBcdBoot(windowsDir, request);
    }

    private static void RunDism(
        string dismPath,
        IReadOnlyList<string> arguments,
        SystemImageJobRequest request,
        string? watchFile = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = dismPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        ConsoleEncoding.ApplyTo(psi);

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException("DISM 을 시작할 수 없습니다.");

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var gate = new object();
        var lastPercent = -1;
        var startedUtc = DateTime.UtcNow;
        Exception? readerFault = null;

        void HandleChunk(string chunk, StringBuilder sink)
        {
            if (string.IsNullOrEmpty(chunk)) return;

            lock (gate)
            {
                sink.Append(chunk);

                foreach (Match match in PercentRegex.Matches(chunk))
                {
                    if (!double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var value))
                        continue;

                    var percent = (int)Math.Clamp(Math.Round(value), 0, 100);
                    // 캡처/적용 본문은 5~90 구간에 매핑해 앞뒤 단계 여유를 둔다.
                    var mapped = 5 + (int)Math.Round(percent * 0.85);
                    if (mapped == lastPercent) continue;
                    lastPercent = mapped;
                    WriteProgress(request, mapped, $"DISM {percent}%");
                }

                if (chunk.Contains('\n') || chunk.Contains('\r'))
                {
                    var text = chunk.Replace('\r', '\n');
                    foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (line.Length is 0 or > 200) continue;
                        if (PercentRegex.IsMatch(line) && line.Length < 40) continue;
                        if (IsDismNoiseLine(line)) continue;
                        WriteProgress(request, null, line);
                    }
                }
            }
        }

        void TryCancelProcess()
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
        }

        var stdoutTask = Task.Run(() =>
        {
            try
            {
                var buffer = new char[512];
                while (true)
                {
                    var read = process.StandardOutput.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;
                    HandleChunk(new string(buffer, 0, read), stdout);
                }
            }
            catch (Exception ex)
            {
                lock (gate) readerFault ??= ex;
            }
        });

        var stderrTask = Task.Run(() =>
        {
            try
            {
                var buffer = new char[512];
                while (true)
                {
                    var read = process.StandardError.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;
                    HandleChunk(new string(buffer, 0, read), stderr);
                }
            }
            catch (Exception ex)
            {
                lock (gate) readerFault ??= ex;
            }
        });

        // DISM 은 리다이렉트 시 % 출력이 늦게 오거나 버퍼링되는 경우가 많다.
        // WIM 파일 성장으로라도 UI가 살아 있음을 보여 준다.
        while (!process.WaitForExit(1000))
        {
            ThrowIfCancelled(request);
            if (File.Exists(request.CancelFile))
            {
                TryCancelProcess();
                throw new OperationCanceledException();
            }

            ReportDismHeartbeat(request, watchFile, startedUtc, ref lastPercent, gate);
        }

        Task.WaitAll(stdoutTask, stderrTask);
        if (readerFault is not null)
            throw new InvalidOperationException("DISM 출력을 읽는 중 오류: " + readerFault.Message, readerFault);

        if (process.ExitCode != 0)
        {
            var detail = stderr.Length > 0 ? stderr.ToString() : stdout.ToString();
            detail = detail.Trim();
            if (detail.Length > 800) detail = detail[^800..];
            throw new InvalidOperationException($"DISM 종료 코드 {process.ExitCode}. {detail}");
        }
    }

    private static void ReportDismHeartbeat(
        SystemImageJobRequest request,
        string? watchFile,
        DateTime startedUtc,
        ref int lastPercent,
        object gate)
    {
        long bytes = 0;
        if (!string.IsNullOrWhiteSpace(watchFile))
        {
            try
            {
                if (File.Exists(watchFile))
                    bytes = new FileInfo(watchFile).Length;
            }
            catch
            {
                // ignore
            }
        }

        var elapsed = DateTime.UtcNow - startedUtc;
        var sizeText = FormatByteSize(bytes);
        var timeText = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss")
            : elapsed.ToString(@"mm\:ss");

        lock (gate)
        {
            // DISM % 가 아직 없으면 3~4% 근처에서 살아있음을 표시.
            if (lastPercent < 5)
            {
                var soft = bytes > 0 ? 4 : 3;
                if (soft != lastPercent)
                    lastPercent = soft;
                WriteProgress(request, soft,
                    bytes > 0
                        ? $"캡처 진행 중… {sizeText} 기록 · 경과 {timeText}"
                        : $"캡처 준비/스캔 중… 경과 {timeText} (잠시 %가 안 변할 수 있음)");
            }
            else if (bytes > 0)
            {
                WriteProgress(request, null, $"기록 중 {sizeText} · 경과 {timeText}");
            }
        }
    }

    private static bool IsDismNoiseLine(string line)
    {
        // "버전: 10.0...." / "Version:" / 저작권 배너 등은 상태줄만 지저분하게 만든다.
        if (line.StartsWith("버전", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.StartsWith("Version", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.StartsWith("Copyright", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.StartsWith("Deployment Image Servicing", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.Contains("Microsoft Corporation", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string FormatByteSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double value = bytes;
        string[] units = ["KB", "MB", "GB", "TB"];
        foreach (var unit in units)
        {
            value /= 1024.0;
            if (value < 1024.0)
                return $"{value:0.#} {unit}";
        }

        return $"{value:0.#} PB";
    }

    private static void TryRunBcdBoot(string windowsDir, SystemImageJobRequest request)
    {
        var bcdboot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "bcdboot.exe");
        if (!File.Exists(bcdboot))
            bcdboot = "bcdboot.exe";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = bcdboot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            ConsoleEncoding.ApplyTo(psi);
            psi.ArgumentList.Add(windowsDir);
            psi.ArgumentList.Add("/f");
            psi.ArgumentList.Add("UEFI");

            using var process = Process.Start(psi)
                                ?? throw new InvalidOperationException("bcdboot 을 시작할 수 없습니다.");
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(60_000);

            if (process.ExitCode != 0)
            {
                WriteProgress(request, null,
                    "bcdboot 자동 구성에 실패했습니다. 복원 후 WinRE에서 수동으로 실행하세요. "
                    + output.Trim());
            }
            else
            {
                WriteProgress(request, 98, "bcdboot 완료");
            }
        }
        catch (Exception ex)
        {
            WriteProgress(request, null, "bcdboot 건너뜀: " + ex.Message);
        }
    }

    private static string CreateShadowCopy(string volumeRoot)
    {
        // volumeRoot 예: C:\
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $volume = '{{volumeRoot.Replace("'", "''")}}'
            $class = [WMIClass]'root\cimv2:Win32_ShadowCopy'
            $result = $class.Create($volume, 'ClientAccessible')
            if ($null -eq $result -or $result.ReturnValue -ne 0) {
              $code = if ($null -eq $result) { 'null' } else { $result.ReturnValue }
              throw "Shadow copy 생성 실패 (ReturnValue=$code). 관리자 권한과 VSS 서비스를 확인하세요."
            }
            Write-Output $result.ShadowID
            """;

        var output = RunPowerShell(script);
        var id = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();

        if (string.IsNullOrWhiteSpace(id) || !id.Contains('{'))
            throw new InvalidOperationException("섀도 복사본 ID를 얻지 못했습니다. " + Truncate(output, 300));

        return id.Trim();
    }

    private static string ResolveShadowDevicePath(string shadowId)
    {
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $id = '{{shadowId.Replace("'", "''")}}'
            $sc = Get-CimInstance -ClassName Win32_ShadowCopy | Where-Object { $_.ID -eq $id } | Select-Object -First 1
            if ($null -eq $sc) { throw "섀도 복사본을 찾지 못했습니다: $id" }
            $device = $sc.DeviceObject
            if (-not $device.EndsWith('\')) { $device = $device + '\' }
            Write-Output $device
            """;

        var output = RunPowerShell(script);
        var path = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault()?.Trim();

        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("섀도 장치 경로를 해석하지 못했습니다.");

        return path;
    }

    /// <summary>
    /// DISM 은 \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN 경로에서
    /// 오래 걸리거나 진행률이 안 나오는 경우가 있어, 디렉터리 심볼릭 링크로 붙인다.
    /// </summary>
    private static string MountShadowLink(string deviceObject)
    {
        var root = Path.Combine(Path.GetTempPath(), "WinCustoms", "shadows");
        Directory.CreateDirectory(root);
        var link = Path.Combine(root, Guid.NewGuid().ToString("N"));

        var target = deviceObject.Trim();
        if (!target.EndsWith('\\'))
            target += "\\";

        // cmd mklink 가 GLOBALROOT 심볼릭 링크를 가장 안정적으로 만든다.
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Arguments = $"/c mklink /D \"{link}\" \"{target}\""
        };
        ConsoleEncoding.ApplyTo(psi);

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException("mklink 를 시작할 수 없습니다.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* */ }
            throw new TimeoutException("mklink 가 시간 초과되었습니다.");
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        if (process.ExitCode != 0 || !Directory.Exists(link))
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException(
                "섀도 복사본 연결(mklink)에 실패했습니다. " + Truncate(detail.Trim(), 300));
        }


        // DISM CaptureDir 은 끝의 \ 없이 주어도 되지만, 디렉터리임을 분명히 한다.
        return link;
    }

    private static void TryUnmountShadowLink(string? link)
    {
        if (string.IsNullOrWhiteSpace(link)) return;
        try
        {
            if (Directory.Exists(link))
                Directory.Delete(link);
        }
        catch
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    Arguments = $"/c rmdir \"{link}\""
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(10_000);
            }
            catch
            {
                // ignore
            }
        }
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // ignore
        }
    }

    private static void TryDeleteShadowCopy(string shadowId)
    {
        try
        {
            var script = $$"""
                $ErrorActionPreference = 'SilentlyContinue'
                $id = '{{shadowId.Replace("'", "''")}}'
                $sc = Get-CimInstance -ClassName Win32_ShadowCopy | Where-Object { $_.ID -eq $id } | Select-Object -First 1
                if ($null -ne $sc) { $sc | Remove-CimInstance }
                """;
            RunPowerShell(script);
        }
        catch
        {
            // 정리 실패는 무시.
        }
    }

    private static string RunPowerShell(string script)
    {
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var powershell = Path.Combine(system32, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell)) powershell = "powershell.exe";

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
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

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException("PowerShell 을 시작할 수 없습니다.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* */ }
            throw new TimeoutException("PowerShell 작업이 시간 초과되었습니다.");
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException(detail.Trim());
        }

        return stdout;
    }

    private static void WriteCompanionFiles(string imageFile, string imageName)
        => SystemImageCompanionFiles.Write(imageFile, imageName);

    private static void WriteProgress(SystemImageJobRequest request, int? percent, string message)
    {
        if (string.IsNullOrWhiteSpace(request.ProgressFile)) return;

        try
        {
            var line = new SystemImageProgressLine
            {
                Percent = percent,
                Message = message,
                UtcTicks = DateTime.UtcNow.Ticks
            };

            var json = JsonSerializer.Serialize(line, WinCustomsJsonContext.Default.SystemImageProgressLine);
            File.AppendAllText(request.ProgressFile, json + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // 진행률 기록 실패는 본 작업을 막지 않는다.
        }
    }

    private static void ThrowIfCancelled(SystemImageJobRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.CancelFile) && File.Exists(request.CancelFile))
            throw new OperationCanceledException();
    }

    private static string NormalizeVolume(string volume)
    {
        var root = Path.GetPathRoot(volume.Trim());
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("캡처 볼륨이 올바르지 않습니다.");
        return root.EndsWith('\\') ? root : root + "\\";
    }

    private static string NormalizeApplyDir(string applyDir)
    {
        var path = applyDir.Trim();
        if (path.Length == 2 && path[1] == ':')
            path += "\\";
        return Path.GetFullPath(path);
    }

    private static string ResolveDismPath()
    {
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var dism = Path.Combine(system32, "dism.exe");
        return File.Exists(dism) ? dism : "dism.exe";
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}
