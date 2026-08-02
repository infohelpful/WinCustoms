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
                    // 라이브 VSS 캡처 대신 WinRE 오프라인 캡처 준비(플래그+부트스트랩).
                    SystemImageAutoRestore.PrepareCapture(request, (p, m) => WriteProgress(request, p, m));
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
        string? watchFile = null,
        string? captureVolumeRoot = null)
    {
        // Capture-Image 는 stdout/stderr 리다이렉트 시 WIM 헤더만 만들고 멈추는 사례가 많다.
        // 진행은 /LogPath + WIM 파일 크기 감시로 추적한다.
        var captureMode = !string.IsNullOrWhiteSpace(watchFile);

        var psi = new ProcessStartInfo
        {
            FileName = dismPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = !captureMode,
            RedirectStandardError = !captureMode
        };
        if (!captureMode)
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
        var lastGrowthUtc = startedUtc;
        long lastBytes = -1;
        var lastCpu = TimeSpan.Zero;
        var lastCpuSampleUtc = startedUtc;
        Exception? readerFault = null;
        var estimatedFinalBytes = EstimateCaptureOutputBytes(captureVolumeRoot);

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

        Task? stdoutTask = null;
        Task? stderrTask = null;

        if (!captureMode)
        {
            stdoutTask = Task.Run(() =>
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

            stderrTask = Task.Run(() =>
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
        }

        while (!process.WaitForExit(1000))
        {
            ThrowIfCancelled(request);
            if (File.Exists(request.CancelFile))
            {
                TryCancelProcess();
                throw new OperationCanceledException();
            }

            if (captureMode)
            {
                ReportDismHeartbeat(
                    request, watchFile, startedUtc, ref lastPercent, ref lastBytes, ref lastGrowthUtc,
                    ref lastCpu, ref lastCpuSampleUtc, gate, process, estimatedFinalBytes);
            }
        }

        if (stdoutTask is not null && stderrTask is not null)
            Task.WaitAll(stdoutTask, stderrTask);

        if (readerFault is not null)
            throw new InvalidOperationException("DISM 출력을 읽는 중 오류: " + readerFault.Message, readerFault);

        if (process.ExitCode != 0)
        {
            var detail = stderr.Length > 0 ? stderr.ToString() : stdout.ToString();
            detail = detail.Trim();
            if (detail.Length == 0 && !string.IsNullOrWhiteSpace(watchFile))
                detail = ReadDismLogTail(watchFile + ".dism.log", 800);

            if (detail.Length > 800) detail = detail[^800..];
            throw new InvalidOperationException(
                $"DISM 종료 코드 {process.ExitCode}. {SummarizeDismFailure(detail)}");
        }
    }

    private static void ReportDismHeartbeat(
        SystemImageJobRequest request,
        string? watchFile,
        DateTime startedUtc,
        ref int lastPercent,
        ref long lastBytes,
        ref DateTime lastGrowthUtc,
        ref TimeSpan lastCpu,
        ref DateTime lastCpuSampleUtc,
        object gate,
        Process process,
        long estimatedFinalBytes)
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

        var now = DateTime.UtcNow;
        var elapsed = now - startedUtc;
        var sizeText = FormatByteSize(bytes);
        var timeText = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss")
            : elapsed.ToString(@"mm\:ss");

        if (bytes > lastBytes)
        {
            lastBytes = bytes;
            lastGrowthUtc = now;
        }

        var cpuActive = false;
        try
        {
            process.Refresh();
            var cpu = process.TotalProcessorTime;
            if (cpu > lastCpu)
            {
                cpuActive = true;
                lastCpu = cpu;
                lastCpuSampleUtc = now;
            }
            else if ((now - lastCpuSampleUtc).TotalSeconds < 15)
            {
                cpuActive = true;
            }
        }
        catch
        {
            cpuActive = true;
        }

        if (bytes < 1_000_000 && elapsed.TotalSeconds >= 30 && !cpuActive)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* */ }
            throw new InvalidOperationException(
                "DISM 캡처가 멈췄습니다. 30초 동안 WIM 이 1MB 미만이고 프로세스도 대기 상태입니다.\n"
                + $"현재 크기: {sizeText}");
        }

        // 로그에 PATH_NOT_FOUND 가 찍히면 종료 코드 기다리지 말고 바로 끊어서 제외·재시도로 넘긴다.
        if (!string.IsNullOrWhiteSpace(watchFile) && elapsed.TotalSeconds >= 5)
        {
            var logTail = ReadDismLogTail(watchFile + ".dism.log", 2500);
            if (logTail.IndexOf("Error opening file", StringComparison.OrdinalIgnoreCase) >= 0
                && (logTail.IndexOf("0x80070003", StringComparison.OrdinalIgnoreCase) >= 0
                    || logTail.IndexOf("PATH_NOT_FOUND", StringComparison.OrdinalIgnoreCase) >= 0
                    || logTail.IndexOf("0x80070006", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* */ }
                throw new InvalidOperationException(
                    "DISM 캡처 실패(접근 불가 파일). " + SummarizeDismFailure(logTail));
            }
        }

        if (bytes < 1_000_000 && elapsed.TotalSeconds >= 600)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* */ }
            throw new InvalidOperationException(
                "DISM 캡처가 10분 동안 기록을 시작하지 못했습니다. VSS/디스크/백신 상태를 확인하세요.");
        }

        if (bytes >= 1_000_000 && (now - lastGrowthUtc).TotalSeconds >= 300 && !cpuActive)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* */ }
            throw new InvalidOperationException(
                $"DISM 캡처가 멈춘 것으로 보입니다. 5분 동안 WIM 크기가 변하지 않았습니다 ({sizeText}).");
        }

        if (bytes >= 1_000_000)
        {
            var soft = estimatedFinalBytes > 0
                ? 5 + (int)Math.Clamp(Math.Round(bytes * 80.0 / estimatedFinalBytes), 0, 80)
                : 5 + (int)Math.Min(80, Math.Log10(Math.Max(bytes, 10)) * 8);

            if (soft > lastPercent)
            {
                lastPercent = soft;
                // percent 갱신은 로그에 안 남기고 상태줄만 (하트비트 접두어).
                WriteProgress(request, soft, $"\u200B캡처 중 {soft}% · {sizeText} · 경과 {timeText}");
                return;
            }

            WriteProgress(request, null, $"\u200B캡처 중 · {sizeText} · 경과 {timeText}");
            return;
        }

        lock (gate)
        {
            WriteProgress(request, null, $"\u200B스캔/준비 중… {sizeText} · 경과 {timeText}");
        }
    }

    private static long EstimateCaptureOutputBytes(string? volumeRoot)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(volumeRoot)) return 0;
            var drive = new DriveInfo(volumeRoot);
            if (!drive.IsReady) return 0;
            var used = Math.Max(0, drive.TotalSize - drive.TotalFreeSpace);
            // Compress:fast 대략치
            return Math.Max(1_000_000_000L, (long)(used * 0.45));
        }
        catch
        {
            return 0;
        }
    }

    private static string ReadDismLogTail(string logPath, int maxChars)
    {
        try
        {
            if (!File.Exists(logPath)) return string.Empty;
            var log = File.ReadAllText(logPath);
            return log.Length <= maxChars ? log : log[^maxChars..];
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string SummarizeDismFailure(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return "자세한 오류는 .dism.log 를 확인하세요.";

        var openMatch = Regex.Match(
            detail,
            @"Error opening file\s*\[([^\]]+)\]",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (openMatch.Success
            || detail.Contains("0x80070003", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("HRESULT=0x80070003", StringComparison.OrdinalIgnoreCase))
        {
            var fileHint = openMatch.Success ? " 파일: " + Truncate(openMatch.Groups[1].Value, 160) : "";
            return "캡처 중 찾을 수 없는 파일/경로가 있어 중단되었습니다(0x80070003)."
                   + fileHint
                   + " 제외 후 자동 재시도합니다. "
                   + Truncate(detail.ReplaceLineEndings(" "), 180);
        }

        return Truncate(detail.ReplaceLineEndings(" "), 400);
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
