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
            EnsureShadowReadable(mountLink);

            var dism = ResolveDismPath();
            var name = string.IsNullOrWhiteSpace(request.ImageName) ? "WinCustoms Backup" : request.ImageName.Trim();
            var description = $"WinCustoms system backup {DateTime.Now:yyyy-MM-dd HH:mm}";
            var logPath = imageFile + ".dism.log";
            var configPath = Path.Combine(directory, "WinCustoms-CaptureExclude.ini");
            WriteCaptureConfig(configPath);

            // CaptureDir 끝의 \ 가 있어야 디렉터리로 안정적으로 인식되는 환경이 있다.
            var captureDir = mountLink.EndsWith('\\') ? mountLink : mountLink + "\\";

            // DISM 은 깨진/클라우드/사라진 파일 하나에서 전체 캡처를 죽인다(0x80070003).
            // 먼저 VSS 트리를 열어보며 접근 불가 경로를 제외 목록에 넣고, 그래도 실패하면 재시도한다.
            var preExcluded = AppendUnreadableExclusions(captureDir, configPath, request);
            WriteProgress(
                request,
                8,
                preExcluded > 0
                    ? $"접근 불가 {preExcluded:N0}개 제외 완료. DISM 캡처 시작…"
                    : "DISM 캡처 시작…");

            const int maxAttempts = 40;
            Exception? lastFailure = null;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                ThrowIfCancelled(request);
                TryDeleteFile(imageFile);
                TryDeleteFile(logPath);

                var attemptPercent = Math.Min(12, 8 + attempt);
                WriteProgress(
                    request,
                    attemptPercent,
                    attempt == 1
                        ? "DISM이 파일을 기록하는 중… (곧 WIM 용량이 커집니다)"
                        : $"깨진 경로 제외 후 재시도 {attempt}/{maxAttempts}…");

                var args = new[]
                {
                    "/Capture-Image",
                    $"/ImageFile:{imageFile}",
                    $"/CaptureDir:{captureDir}",
                    $"/Name:{name}",
                    $"/Description:{description}",
                    "/Compress:fast",
                    "/NoRpFix",
                    $"/ConfigFile:{configPath}",
                    $"/ScratchDir:{scratchDir}",
                    $"/LogPath:{logPath}",
                    "/LogLevel:3"
                };

                try
                {
                    RunDism(dism, args, request, watchFile: imageFile, captureVolumeRoot: volume);
                    lastFailure = null;
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastFailure = ex;
                    var detail = ex.Message + "\n" + ReadDismLogTail(logPath, 4000);
                    if (attempt >= maxAttempts || !TryAppendExclusionFromDismFailure(detail, captureDir, configPath, out var excluded))
                        throw;

                    WriteProgress(request, attemptPercent, "제외 후 다시 시도: " + TruncateForUi(excluded, 90));
                }
            }

            if (lastFailure is not null)
                throw lastFailure;

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

    private static void WriteCaptureConfig(string path)
    {
        // DISM 은 존재하지 않는/깨진 경로 하나에서 전체 캡처를 중단한다(0x80070003).
        // 기본 제외 + 흔한 임시·캐시·SDK·정션 문제를 빼 둔다. 나머지는 실패 시 동적 추가.
        const string contents =
            """
            [ExclusionList]
            \$ntfs.log
            \hiberfil.sys
            \pagefile.sys
            \swapfile.sys
            \System Volume Information
            \System Volume Information\*
            \$RECYCLE.BIN
            \$RECYCLE.BIN\*
            \RECYCLER
            \RECYCLER\*
            \Windows\CSC
            \Windows\CSC\*
            \Windows\Temp
            \Windows\Temp\*
            \Windows\SoftwareDistribution\Download
            \Windows\SoftwareDistribution\Download\*
            \Windows\Prefetch
            \Windows\Prefetch\*
            \Windows\Installer\$PatchCache$
            \Windows\Installer\$PatchCache$\*
            \Temp
            \Temp\*
            \ProgramData\Microsoft\Windows\WER
            \ProgramData\Microsoft\Windows\WER\*
            \ProgramData\Microsoft\Windows Defender\Scans\History
            \ProgramData\Microsoft\Windows Defender\Scans\History\*
            \ProgramData\DAUM
            \ProgramData\DAUM\*
            \Program Files\dotnet
            \Program Files\dotnet\*
            \Program Files (x86)\dotnet
            \Program Files (x86)\dotnet\*
            \Program Files\WindowsApps
            \Program Files\WindowsApps\*
            \Program Files\DAUM
            \Program Files\DAUM\*
            \Program Files (x86)\DAUM
            \Program Files (x86)\DAUM\*
            \Users\*\AppData\Local\Temp
            \Users\*\AppData\Local\Temp\*
            \Users\*\AppData\Local\Microsoft\Windows\INetCache
            \Users\*\AppData\Local\Microsoft\Windows\INetCache\*
            \Users\*\AppData\Local\CrashDumps
            \Users\*\AppData\Local\CrashDumps\*
            \Users\*\AppData\Local\Packages
            \Users\*\AppData\Local\Packages\*
            \Users\*\AppData\Local\npm-cache
            \Users\*\AppData\Local\npm-cache\*
            \Users\*\AppData\Local\NuGet
            \Users\*\AppData\Local\NuGet\*
            \Users\*\.nuget
            \Users\*\.nuget\*
            \Users\*\AppData\Local\Microsoft\VisualStudio
            \Users\*\AppData\Local\Microsoft\VisualStudio\*
            \Users\*\AppData\Local\Packages\*\TempState
            \Users\*\AppData\Local\Packages\*\TempState\*
            \Users\*\OneDrive
            \Users\*\OneDrive\*
            \Users\*\OneDrive - *
            \Users\*\OneDrive - *\*
            \Users\*\Dropbox
            \Users\*\Dropbox\*
            \Users\*\Google Drive
            \Users\*\Google Drive\*
            \Users\*\iCloudDrive
            \Users\*\iCloudDrive\*

            [CompressionExclusionList]
            *.zip
            *.cab
            *.mp3
            *.mp4
            *.mkv
            *.7z
            *.rar
            """;

        File.WriteAllText(path, contents.Replace("\r\n", "\n").Replace("\n", "\r\n"), Encoding.Unicode);
    }

    /// <summary>
    /// VSS 마운트에서 실제로 열리지 않는 파일/클라우드 스텁을 찾아 ConfigFile 에 넣는다.
    /// DISM 이 이런 항목에서 0x80070003 으로 전체 실패하는 것을 막기 위함이다.
    /// </summary>
    private static int AppendUnreadableExclusions(
        string captureDir,
        string configPath,
        SystemImageJobRequest request)
    {
        var root = captureDir.TrimEnd('\\');
        var found = new List<string>(256);
        long scanned = 0;
        var lastReport = DateTime.UtcNow;
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            ThrowIfCancelled(request);
            var dir = stack.Pop();

            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(dir);
            }
            catch
            {
                AddRelativeExclusion(root, dir, found);
                continue;
            }

            foreach (var entry in entries)
            {
                scanned++;
                if ((DateTime.UtcNow - lastReport).TotalMilliseconds >= 750)
                {
                    var pct = 3 + (int)Math.Min(4, scanned / 250_000.0 * 4);
                    WriteProgress(
                        request,
                        pct,
                        $"캡처 전 검사 중… {scanned:N0}개 확인, 제외 {found.Count:N0}개");
                    lastReport = DateTime.UtcNow;
                }

                var relative = ToCaptureRelative(root, entry);
                if (relative is null || IsStaticallyExcludedRelative(relative))
                    continue;

                FileAttributes attr;
                try
                {
                    attr = File.GetAttributes(entry);
                }
                catch
                {
                    AddRelativeExclusion(root, entry, found);
                    continue;
                }

                var isDir = (attr & FileAttributes.Directory) != 0;

                // OneDrive Files On Demand 등 — DISM 이 열다 죽는 항목.
                // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x00400000 (일부 TFM 에 열거형 없음)
                const FileAttributes recallOnDataAccess = (FileAttributes)0x00400000;
                if ((attr & (FileAttributes.Offline | recallOnDataAccess)) != 0)
                {
                    found.Add(relative);
                    continue;
                }

                if (isDir)
                {
                    // 디렉터리 정션/심볼릭 링크는 재귀하지 않는다(/NoRpFix 와 동일 취지).
                    if ((attr & FileAttributes.ReparsePoint) != 0)
                        continue;

                    stack.Push(entry);
                    continue;
                }

                // Windows 트리는 대체로 정상이고 파일이 너무 많아 전수 Open 은 비현실적이다.
                // 실제 실패가 잦은 Program Files / ProgramData / Users 만 연다.
                if (relative.StartsWith(@"\Windows\", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    using var stream = new FileStream(
                        entry,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                }
                catch
                {
                    found.Add(relative);
                }
            }
        }

        if (found.Count == 0)
            return 0;

        // 같은 폴더에서 여러 개가 깨지면 부모 디렉터리를 통째로 제외해 목록을 줄인다.
        var collapsed = CollapseExclusionPaths(found);
        AppendExclusionLines(configPath, collapsed.ToArray());
        return collapsed.Count;
    }

    private static List<string> CollapseExclusionPaths(List<string> relatives)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in relatives)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var normalized = path.StartsWith('\\') ? path : "\\" + path;
            set.Add(normalized);

            var parent = Path.GetDirectoryName(normalized.TrimStart('\\'));
            if (string.IsNullOrEmpty(parent)) continue;
            var parentRel = "\\" + parent.Replace('/', '\\');

            // 같은 부모 아래 실패가 3개 이상이면 부모를 제외.
            var siblings = 0;
            foreach (var other in relatives)
            {
                var o = other.StartsWith('\\') ? other : "\\" + other;
                if (o.StartsWith(parentRel + "\\", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(o, parentRel, StringComparison.OrdinalIgnoreCase))
                    siblings++;
                if (siblings >= 3) break;
            }

            if (siblings >= 3)
                set.Add(parentRel);
        }

        return set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? ToCaptureRelative(string root, string fullPath)
    {
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return null;
        var relative = fullPath[root.Length..];
        if (string.IsNullOrEmpty(relative))
            return null;
        return relative.StartsWith('\\') ? relative : "\\" + relative;
    }

    private static void AddRelativeExclusion(string root, string fullPath, List<string> sink)
    {
        var relative = ToCaptureRelative(root, fullPath);
        if (relative is not null)
            sink.Add(relative);
    }

    private static bool IsStaticallyExcludedRelative(string relative)
    {
        var r = relative.Replace('/', '\\');

        ReadOnlySpan<string> prefixes =
        [
            @"\System Volume Information",
            @"\$RECYCLE.BIN",
            @"\RECYCLER",
            @"\Windows\CSC",
            @"\Windows\Temp",
            @"\Windows\Prefetch",
            @"\Windows\SoftwareDistribution\Download",
            @"\Windows\Installer\$PatchCache$",
            @"\Temp",
            @"\ProgramData\Microsoft\Windows\WER",
            @"\ProgramData\Microsoft\Windows Defender\Scans\History",
            @"\ProgramData\DAUM",
            @"\Program Files\dotnet",
            @"\Program Files (x86)\dotnet",
            @"\Program Files\WindowsApps",
            @"\Program Files\DAUM",
            @"\Program Files (x86)\DAUM",
        ];

        foreach (var prefix in prefixes)
        {
            if (r.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || r.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // \Users\<name>\...
        if (r.StartsWith(@"\Users\", StringComparison.OrdinalIgnoreCase))
        {
            var rest = r[@"\Users\".Length..];
            var slash = rest.IndexOf('\\');
            if (slash > 0)
            {
                var afterUser = rest[slash..];
                if (afterUser.StartsWith(@"\AppData\Local\Temp", StringComparison.OrdinalIgnoreCase)
                    || afterUser.StartsWith(@"\AppData\Local\Microsoft\Windows\INetCache", StringComparison.OrdinalIgnoreCase)
                    || afterUser.StartsWith(@"\AppData\Local\CrashDumps", StringComparison.OrdinalIgnoreCase)
                    || afterUser.StartsWith(@"\AppData\Local\Packages", StringComparison.OrdinalIgnoreCase)
                    || afterUser.StartsWith(@"\AppData\Local\npm-cache", StringComparison.OrdinalIgnoreCase)
                    || afterUser.StartsWith(@"\AppData\Local\NuGet", StringComparison.OrdinalIgnoreCase)
                    || afterUser.StartsWith(@"\.nuget", StringComparison.OrdinalIgnoreCase)
                    || afterUser.StartsWith(@"\AppData\Local\Microsoft\VisualStudio", StringComparison.OrdinalIgnoreCase)
                    || afterUser.StartsWith(@"\OneDrive", StringComparison.OrdinalIgnoreCase)
                    || afterUser.StartsWith(@"\Dropbox", StringComparison.OrdinalIgnoreCase)
                    || afterUser.StartsWith(@"\Google Drive", StringComparison.OrdinalIgnoreCase)
                    || afterUser.StartsWith(@"\iCloudDrive", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private static bool TryAppendExclusionFromDismFailure(
        string detail,
        string captureDir,
        string configPath,
        out string excludedRelative)
    {
        excludedRelative = "";
        if (detail.IndexOf("0x80070003", StringComparison.OrdinalIgnoreCase) < 0
            && detail.IndexOf("PATH_NOT_FOUND", StringComparison.OrdinalIgnoreCase) < 0
            && detail.IndexOf("Error opening file", StringComparison.OrdinalIgnoreCase) < 0)
            return false;

        var match = Regex.Match(
            detail,
            @"Error opening file\s*\[([^\]]+)\]",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return false;

        var fullPath = match.Groups[1].Value.Trim();
        var root = captureDir.TrimEnd('\\');
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return false;

        var relative = fullPath[root.Length..];
        if (string.IsNullOrWhiteSpace(relative))
            return false;
        if (!relative.StartsWith('\\'))
            relative = "\\" + relative;

        // 같은 트리에서 연쇄 실패가 많아 파일·부모·조부모를 함께 제외해 재시도 횟수를 줄인다.
        var relNoSlash = relative.TrimStart('\\');
        var parent = Path.GetDirectoryName(relNoSlash);
        var parentRelative = string.IsNullOrEmpty(parent) ? null : "\\" + parent.Replace('/', '\\');
        var grand = string.IsNullOrEmpty(parent) ? null : Path.GetDirectoryName(parent);
        var grandRelative = string.IsNullOrEmpty(grand) ? null : "\\" + grand.Replace('/', '\\');

        excludedRelative = grandRelative ?? parentRelative ?? relative;
        return AppendExclusionLines(configPath, relative, parentRelative, grandRelative);
    }

    private static bool AppendExclusionLines(string configPath, params string?[] lines)
    {
        var existing = File.Exists(configPath)
            ? File.ReadAllText(configPath, Encoding.Unicode)
            : "[ExclusionList]\r\n";

        var toAdd = new List<string>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var normalized = line.Trim();
            if (existing.IndexOf(normalized, StringComparison.OrdinalIgnoreCase) >= 0)
                continue;
            if (toAdd.Exists(x => string.Equals(x, normalized, StringComparison.OrdinalIgnoreCase)))
                continue;
            toAdd.Add(normalized);
            // DISM 이 디렉터리 와일드카드를 요구하는 경우가 있어 함께 기록.
            if (!normalized.EndsWith('*') && !Path.HasExtension(normalized.TrimStart('\\')))
            {
                var starred = normalized.TrimEnd('\\') + "\\*";
                if (existing.IndexOf(starred, StringComparison.OrdinalIgnoreCase) < 0
                    && !toAdd.Exists(x => string.Equals(x, starred, StringComparison.OrdinalIgnoreCase)))
                    toAdd.Add(starred);
            }
        }

        if (toAdd.Count == 0)
            return false;

        if (!existing.EndsWith("\n", StringComparison.Ordinal))
            existing += "\r\n";
        existing += string.Join("\r\n", toAdd) + "\r\n";
        File.WriteAllText(configPath, existing, Encoding.Unicode);
        return true;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }

    private static string TruncateForUi(string value, int max)
        => Truncate(value, max);

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

    private static void EnsureShadowReadable(string mountLink)
    {
        try
        {
            var probe = Path.Combine(mountLink, "Windows", "System32", "ntoskrnl.exe");
            if (File.Exists(probe))
                return;

            if (Directory.Exists(Path.Combine(mountLink, "Windows")))
                return;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "섀도 복사본을 읽지 못했습니다. VSS/심볼릭 링크 상태를 확인하세요. " + ex.Message);
        }

        throw new InvalidOperationException(
            "섀도 마운트에 Windows 폴더가 없습니다. 캡처를 진행할 수 없습니다: " + mountLink);
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
        // 경로가 짧아야 캡처 중 MAX_PATH / 깨진 하위 경로 실패가 줄어든다.
        var link = Path.Combine(
            Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\",
            "wcvss");

        TryUnmountShadowLink(link);
        Directory.CreateDirectory(Path.GetDirectoryName(link.TrimEnd('\\'))!);

        var target = deviceObject.Trim();
        if (!target.EndsWith('\\'))
            target += "\\";

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
