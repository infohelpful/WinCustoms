using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinCustoms.Common;

/// <summary>
/// 순정 Win11 ISO → 트윅/디블로트 이식 → 커스텀 설치 ISO.
/// 승격 프로세스에서 XAML 없이 실행된다.
/// </summary>
public static class CustomIsoJobHost
{
    public const string JobSwitch = "--custom-iso-job";

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

        CustomIsoJobRequest? request = null;
        var result = new CustomIsoJobResult();

        try
        {
            var json = File.ReadAllText(jobPath);
            request = JsonSerializer.Deserialize(json, WinCustomsJsonContext.Default.CustomIsoJobRequest)
                      ?? throw new InvalidOperationException("커스텀 ISO 작업 파일을 해석할 수 없습니다.");

            Build(request);
            result.Success = true;
            result.OutputIsoPath = request.OutputIsoPath;
            Progress(request, 100, "완료");
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.Error = "작업이 취소되었습니다.";
            if (request is not null) Progress(request, null, result.Error);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            if (request is not null) Progress(request, null, "오류: " + ex.Message);
        }

        try
        {
            var resultPath = request?.ResultFile ?? (jobPath + ".result");
            File.WriteAllText(resultPath, JsonSerializer.Serialize(result, WinCustomsJsonContext.Default.CustomIsoJobResult));
        }
        catch
        {
            // ignore
        }

        return result.Success ? 0 : 1;
    }

    private static void Build(CustomIsoJobRequest request)
    {
        ThrowIfCancelled(request);

        if (!File.Exists(request.SourceIsoPath))
            throw new FileNotFoundException("순정 ISO를 찾을 수 없습니다.", request.SourceIsoPath);

        var oscdimg = FindOscdimg()
                      ?? throw new InvalidOperationException(
                          "oscdimg.exe 를 찾을 수 없습니다. 배포본 Tools\\oscdimg\\oscdimg.exe 가 포함돼 있는지 확인하세요.\n"
                          + "https://learn.microsoft.com/windows-hardware/get-started/adk-install");

        var work = string.IsNullOrWhiteSpace(request.WorkDirectory)
            ? Path.Combine(Path.GetTempPath(), "WinCustoms", "IsoBuild-" + Guid.NewGuid().ToString("N"))
            : request.WorkDirectory;

        var extractDir = Path.Combine(work, "iso");
        var mountDir = Path.Combine(work, "mount");
        Directory.CreateDirectory(extractDir);
        Directory.CreateDirectory(mountDir);

        var mounted = false;

        try
        {
            Progress(request, 5, "ISO 내용 추출 중...");
            ExtractIso(request.SourceIsoPath, extractDir, request);

            ThrowIfCancelled(request);
            var installMedia = FindInstallMedia(extractDir);
            ClearReadOnlyAttribute(installMedia);
            Progress(request, 20, "설치 이미지: " + Path.GetFileName(installMedia));

            var wimPath = installMedia;
            var mountIndex = request.ImageIndex;
            if (installMedia.EndsWith(".esd", StringComparison.OrdinalIgnoreCase))
            {
                Progress(request, 25, "ESD → WIM 변환 중...");
                wimPath = Path.Combine(extractDir, "sources", "install.wim");
                ExportEsdToWim(installMedia, wimPath, request.ImageIndex, request);
                TryDelete(installMedia);
                mountIndex = 1;
            }

            // ISO에서 복사된 WIM 은 읽기 전용인 경우가 많고, 그대로면 DISM 0xc1510111 이 난다.
            ClearReadOnlyAttribute(wimPath);
            ClearReadOnlyAttribute(Path.Combine(extractDir, "sources", "boot.wim"));

            ThrowIfCancelled(request);
            Progress(request, 35,
                $"install.wim 마운트 (index {mountIndex})… 용량에 따라 수 분~십수 분 걸릴 수 있습니다");
            RunDism([
                "/Mount-Image",
                $"/ImageFile:{wimPath}",
                $"/Index:{mountIndex}",
                $"/MountDir:{mountDir}"
            ], request);
            mounted = true;

            ThrowIfCancelled(request);
            if (request.RegistryOperations.Count > 0)
            {
                Progress(request, 50, $"오프라인 레지스트리 적용 ({request.RegistryOperations.Count}건)...");
                OfflineRegistryApplier.Apply(mountDir, request.RegistryOperations, m => Progress(request, null, m));
            }

            ThrowIfCancelled(request);
            if (request.AppxPackageNames.Count > 0)
            {
                Progress(request, 60, "프로비저닝 앱 제거 중...");
                RemoveProvisionedApps(mountDir, request.AppxPackageNames, request);
            }

            string? driversDir = null;
            if (request.InjectHostDrivers)
            {
                ThrowIfCancelled(request);
                driversDir = Path.Combine(work, "drivers");
                Progress(request, 66, "현재 PC 드라이버 내보내기...");
                ExportOnlineDrivers(driversDir, request);
                Progress(request, 70, "install.wim에 드라이버 주입...");
                AddDriversToImage(mountDir, driversDir, request);
            }

            if (request.BypassSetupRequirements)
            {
                ThrowIfCancelled(request);
                Progress(request, 74, "install.wim 설치 검사 완화(MoSetup)...");
                ApplyInstallImageBypass(mountDir, request);
            }

            ThrowIfCancelled(request);
            Progress(request, 78, "install.wim 저장(언마운트)...");
            RunDism(["/Unmount-Image", $"/MountDir:{mountDir}", "/Commit"], request);
            mounted = false;

            if (request.BypassSetupRequirements || request.InjectHostDrivers)
            {
                ThrowIfCancelled(request);
                Progress(request, 82, "boot.wim 처리 중...");
                PatchBootWim(
                    Path.Combine(extractDir, "sources", "boot.wim"),
                    mountDir,
                    request.BypassSetupRequirements,
                    request.InjectHostDrivers ? driversDir : null,
                    request);
            }

            ThrowIfCancelled(request);
            Progress(request, 90, "커스텀 ISO 생성 중 (oscdimg)...");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.OutputIsoPath))!);
            if (File.Exists(request.OutputIsoPath))
                File.Delete(request.OutputIsoPath);

            BuildIso(oscdimg, extractDir, request.OutputIsoPath, request);
            Progress(request, 98, "ISO 생성 완료");
        }
        finally
        {
            if (mounted)
            {
                try
                {
                    RunDism(["/Unmount-Image", $"/MountDir:{mountDir}", "/Discard"], request, ignoreExit: true);
                }
                catch
                {
                    // ignore
                }
            }
        }
    }

    private static void ExtractIso(string isoPath, string destDir, CustomIsoJobRequest request)
    {
        // Mount-DiskImage 로 마운트 후 robocopy
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $iso = '{{isoPath.Replace("'", "''")}}'
            $dest = '{{destDir.Replace("'", "''")}}'
            New-Item -ItemType Directory -Force -Path $dest | Out-Null
            $img = Mount-DiskImage -ImagePath $iso -PassThru
            try {
              $letter = ($img | Get-Volume).DriveLetter
              if (-not $letter) { throw 'ISO 드라이브 문자를 얻지 못했습니다.' }
              $src = ($letter.ToString() + ':\')
              & robocopy.exe $src $dest /E /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
              $code = $LASTEXITCODE
              if ($code -ge 8) { throw "robocopy 실패 코드 $code" }
            }
            finally {
              Dismount-DiskImage -ImagePath $iso | Out-Null
            }
            """;

        RunPowerShell(script, request);
    }

    private static string FindInstallMedia(string extractDir)
    {
        var sources = Path.Combine(extractDir, "sources");
        var wim = Path.Combine(sources, "install.wim");
        if (File.Exists(wim)) return wim;
        var esd = Path.Combine(sources, "install.esd");
        if (File.Exists(esd)) return esd;
        throw new FileNotFoundException("sources\\install.wim / install.esd 를 찾을 수 없습니다. 순정 Windows ISO인지 확인하세요.");
    }

    private static void ExportEsdToWim(string esdPath, string wimPath, int index, CustomIsoJobRequest request)
    {
        if (File.Exists(wimPath)) File.Delete(wimPath);
        RunDism([
            "/Export-Image",
            $"/SourceImageFile:{esdPath}",
            $"/SourceIndex:{index}",
            $"/DestinationImageFile:{wimPath}",
            "/Compress:max",
            "/CheckIntegrity"
        ], request);
    }

    private static void ApplyInstallImageBypass(string mountDir, CustomIsoJobRequest request)
    {
        // 업그레이드/일부 Setup 경로용. 클린 설치 검사는 주로 boot.wim LabConfig.
        var ops = new List<RegistryOperation>
        {
            RegistryOperation.Set(
                RegistryRoot.LocalMachine,
                @"SYSTEM\Setup\MoSetup",
                "AllowUpgradesWithUnsupportedTPMOrCPU",
                Microsoft.Win32.RegistryValueKind.DWord,
                1)
        };
        OfflineRegistryApplier.Apply(mountDir, ops, m => Progress(request, null, m));
    }

    private static void ApplyBootImageBypass(string mountDir, CustomIsoJobRequest request)
    {
        var lab = @"SYSTEM\Setup\LabConfig";
        var names = new[]
        {
            "BypassTPMCheck",
            "BypassSecureBootCheck",
            "BypassRAMCheck",
            "BypassCPUCheck",
            "BypassStorageCheck",
            "BypassDiskCheck"
        };

        var ops = names
            .Select(n => RegistryOperation.Set(
                RegistryRoot.LocalMachine, lab, n, Microsoft.Win32.RegistryValueKind.DWord, 1))
            .ToList();

        OfflineRegistryApplier.Apply(mountDir, ops, m => Progress(request, null, m));
    }

    private static void ExportOnlineDrivers(string destination, CustomIsoJobRequest request)
    {
        Directory.CreateDirectory(destination);
        RunDism([
            "/Online",
            "/Export-Driver",
            "/Destination:" + destination
        ], request);
    }

    private static void AddDriversToImage(string mountDir, string driversDir, CustomIsoJobRequest request)
    {
        if (!Directory.Exists(driversDir))
            throw new DirectoryNotFoundException("내보낸 드라이버 폴더가 없습니다: " + driversDir);

        // 일부 서드파티 드라이버는 실패할 수 있음 — 가능한 것만 넣고 계속
        RunDism([
            "/Image:" + mountDir,
            "/Add-Driver",
            "/Driver:" + driversDir,
            "/Recurse"
        ], request, ignoreExit: true);
    }

    private static void PatchBootWim(
        string bootWim,
        string mountDir,
        bool bypass,
        string? driversDir,
        CustomIsoJobRequest request)
    {
        if (!File.Exists(bootWim))
            throw new FileNotFoundException("sources\\boot.wim 이 없습니다. 순정 설치 ISO인지 확인하세요.", bootWim);

        ClearReadOnlyAttribute(bootWim);

        var indexes = GetImageInfos(bootWim);
        if (indexes.Count == 0)
            throw new InvalidOperationException("boot.wim 인덱스를 읽지 못했습니다.");

        foreach (var info in indexes)
        {
            ThrowIfCancelled(request);
            Progress(request, null, $"boot.wim index {info.Index} ({info.Name}) 마운트...");

            if (Directory.Exists(mountDir))
            {
                try { Directory.Delete(mountDir, recursive: true); } catch { /* */ }
            }

            Directory.CreateDirectory(mountDir);

            var mounted = false;
            try
            {
                RunDism([
                    "/Mount-Image",
                    $"/ImageFile:{bootWim}",
                    $"/Index:{info.Index}",
                    $"/MountDir:{mountDir}"
                ], request);
                mounted = true;

                if (bypass)
                {
                    Progress(request, null, $"boot.wim[{info.Index}] LabConfig 우회 적용...");
                    ApplyBootImageBypass(mountDir, request);
                }

                if (!string.IsNullOrWhiteSpace(driversDir) && Directory.Exists(driversDir))
                {
                    Progress(request, null, $"boot.wim[{info.Index}] 드라이버 주입...");
                    AddDriversToImage(mountDir, driversDir, request);
                }

                RunDism(["/Unmount-Image", $"/MountDir:{mountDir}", "/Commit"], request);
                mounted = false;
            }
            finally
            {
                if (mounted)
                {
                    try
                    {
                        RunDism(["/Unmount-Image", $"/MountDir:{mountDir}", "/Discard"], request, ignoreExit: true);
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }
        }
    }

    private static void RemoveProvisionedApps(string mountDir, List<string> names, CustomIsoJobRequest request)
    {
        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var listScript = $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            Get-AppxProvisionedPackage -Path '{{mountDir.Replace("'", "''")}}' |
              ForEach-Object { $_.PackageName + '|' + $_.DisplayName }
            """;
        var output = RunPowerShellCapture(listScript, request);
        var packages = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var line in packages)
        {
            ThrowIfCancelled(request);
            var packageName = line.Split('|')[0].Trim();
            if (string.IsNullOrEmpty(packageName)) continue;

            // PackageName 형식: Name_Version_... — 앞 토큰과 카탈로그 이름 매칭
            var shortName = packageName.Split('_')[0];
            var match = wanted.Any(w =>
                packageName.StartsWith(w + "_", StringComparison.OrdinalIgnoreCase)
                || string.Equals(shortName, w, StringComparison.OrdinalIgnoreCase)
                || packageName.Contains(w, StringComparison.OrdinalIgnoreCase));

            if (!match) continue;

            Progress(request, null, "제거: " + packageName);
            try
            {
                RunDism([
                    "/Image:" + mountDir,
                    "/Remove-ProvisionedAppxPackage",
                    "/PackageName:" + packageName
                ], request, ignoreExit: true);
            }
            catch (Exception ex)
            {
                Progress(request, null, "제거 실패(무시): " + ex.Message);
            }
        }
    }

    private static void BuildIso(string oscdimg, string extractDir, string outputIso, CustomIsoJobRequest request)
    {
        var etfsboot = Path.Combine(extractDir, "boot", "etfsboot.com");
        var efisys = Path.Combine(extractDir, "efi", "microsoft", "boot", "efisys.bin");
        if (!File.Exists(etfsboot) || !File.Exists(efisys))
            throw new FileNotFoundException("부팅 파일(boot\\etfsboot.com 또는 efi\\microsoft\\boot\\efisys.bin)이 없습니다.");

        // ArgumentList 는 -bootdata 안의 "경로" 따옴표를 ""경로"" 로 이스케이프해서
        // oscdimg Error 123 을 낸다. 공백 없는 짧은 경로로 복사해 따옴표 없이 넘긴다.
        var staging = Path.Combine(
            Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\",
            "wc-oscd");
        Directory.CreateDirectory(staging);
        var etfsCopy = Path.Combine(staging, "etfsboot.com");
        var efiCopy = Path.Combine(staging, "efisys.bin");

        try
        {
            File.Copy(etfsboot, etfsCopy, overwrite: true);
            File.Copy(efisys, efiCopy, overwrite: true);

            var bootData = $"2#p0,e,b{etfsCopy}#pEF,e,b{efiCopy}";
            RunProcess(oscdimg,
            [
                "-m", "-o", "-u2", "-udfver102",
                "-bootdata:" + bootData,
                extractDir,
                outputIso
            ], request);
        }
        finally
        {
            try { if (File.Exists(etfsCopy)) File.Delete(etfsCopy); } catch { /* */ }
            try { if (File.Exists(efiCopy)) File.Delete(efiCopy); } catch { /* */ }
        }
    }

    public static string? FindOscdimg()
    {
        // 1) 앱 동봉본 (ADK 설치 불필요)
        foreach (var bundled in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "Tools", "oscdimg", "oscdimg.exe"),
                     Path.Combine(AppContext.BaseDirectory, "oscdimg.exe"),
                 })
        {
            if (File.Exists(bundled))
                return bundled;
        }

        // 2) 시스템에 설치된 Windows ADK (있으면 사용)
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Windows Kits", "10", "Assessment and Deployment Kit", "Deployment Tools", "amd64", "Oscdimg", "oscdimg.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Windows Kits", "10", "Assessment and Deployment Kit", "Deployment Tools", "amd64", "Oscdimg", "oscdimg.exe"),
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }

        // 3) PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var c = Path.Combine(dir.Trim(), "oscdimg.exe");
                if (File.Exists(c)) return c;
            }
            catch
            {
                // skip
            }
        }

        return null;
    }

    public static IReadOnlyList<WindowsImageInfo> GetImageInfos(string imageFile)
    {
        var dism = ResolveDism();
        var psi = new ProcessStartInfo
        {
            FileName = dism,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        ConsoleEncoding.ApplyTo(psi);
        psi.ArgumentList.Add("/Get-ImageInfo");
        psi.ArgumentList.Add("/ImageFile:" + imageFile);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("DISM 실행 실패");
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(120_000))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* */ }
            throw new TimeoutException("DISM /Get-ImageInfo 가 시간 초과되었습니다.");
        }

        var output = stdoutTask.GetAwaiter().GetResult() + stderrTask.GetAwaiter().GetResult();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"DISM 종료 코드 {p.ExitCode}. {output.Trim()}");

        var list = ParseImageInfo(output);
        if (list.Count == 0)
            throw new InvalidOperationException("이미지 인덱스 정보를 해석하지 못했습니다.");
        return list;
    }

    internal static List<WindowsImageInfo> ParseImageInfo(string output)
    {
        var list = new List<WindowsImageInfo>();
        WindowsImageInfo? current = null;

        foreach (var raw in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            // 전각 콜론(：) 도 허용. 한국어 DISM 은 "인덱스" / 일부 환경 "색인".
            var indexMatch = Regex.Match(line,
                @"^(?:Index|인덱스|색인)\s*[:：]\s*(\d+)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (indexMatch.Success)
            {
                if (current is not null) list.Add(current);
                current = new WindowsImageInfo { Index = int.Parse(indexMatch.Groups[1].Value, CultureInfo.InvariantCulture) };
                continue;
            }

            if (current is null) continue;

            var nameMatch = Regex.Match(line, @"^(?:Name|이름)\s*[:：]\s*(.+)$", RegexOptions.IgnoreCase);
            if (nameMatch.Success)
            {
                current.Name = nameMatch.Groups[1].Value.Trim();
                continue;
            }

            var descMatch = Regex.Match(line, @"^(?:Description|설명)\s*[:：]\s*(.+)$", RegexOptions.IgnoreCase);
            if (descMatch.Success)
            {
                current.Description = descMatch.Groups[1].Value.Trim();
                continue;
            }

            var sizeMatch = Regex.Match(line, @"^(?:Size|크기)\s*[:：]\s*([\d,]+)", RegexOptions.IgnoreCase);
            if (sizeMatch.Success
                && long.TryParse(sizeMatch.Groups[1].Value.Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
            {
                current.SizeBytes = size;
            }
        }

        if (current is not null) list.Add(current);
        return list;
    }

    private static readonly Regex PercentRegex = new(
        @"(\d{1,3}(?:\.\d+)?)\s*%",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static void RunDism(IReadOnlyList<string> args, CustomIsoJobRequest request, bool ignoreExit = false)
    {
        ThrowIfCancelled(request);
        RunProcess(ResolveDism(), args, request, ignoreExit);
    }

    private static string ResolveDism()
    {
        var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dism.exe");
        return File.Exists(p) ? p : "dism.exe";
    }

    private static void RunProcess(string file, IReadOnlyList<string> args, CustomIsoJobRequest request, bool ignoreExit = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        ConsoleEncoding.ApplyTo(psi);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException(file + " 실행 실패");
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var gate = new object();
        var lastStatus = Path.GetFileName(file) + " 실행 중…";
        var startedUtc = DateTime.UtcNow;
        var lastReportedPercent = -1;
        Exception? readerFault = null;

        void HandleChunk(string chunk, StringBuilder sink)
        {
            if (string.IsNullOrEmpty(chunk)) return;
            lock (gate)
            {
                sink.Append(chunk);

                foreach (Match match in PercentRegex.Matches(chunk))
                {
                    if (double.TryParse(match.Groups[1].Value, NumberStyles.Float,
                            CultureInfo.InvariantCulture, out var value))
                    {
                        var percent = (int)Math.Clamp(Math.Round(value), 0, 100);
                        if (percent == lastReportedPercent) continue;
                        lastReportedPercent = percent;
                        lastStatus = $"{Path.GetFileName(file)} {percent}%";
                        // 마운트 구간(35)과 다음 단계(50) 사이에 살짝 반영
                        var mapped = 35 + (int)Math.Round(percent * 0.14);
                        // \u200B → UI 상태줄만, 로그에 % 도배 안 함
                        Progress(request, mapped, "\u200B" + lastStatus);
                    }
                }

                if (chunk.Contains('\n') || chunk.Contains('\r'))
                {
                    foreach (var line in chunk.Replace('\r', '\n')
                                 .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (line.Length is 0 or > 180) continue;
                        if (line.StartsWith("버전", StringComparison.OrdinalIgnoreCase)) continue;
                        if (line.StartsWith("Version", StringComparison.OrdinalIgnoreCase)) continue;
                        if (line.StartsWith("Copyright", StringComparison.OrdinalIgnoreCase)) continue;
                        if (PercentRegex.IsMatch(line) && line.Length < 40) continue;
                        lastStatus = line;
                    }
                }
            }
        }

        var stdoutTask = Task.Run(() =>
        {
            try
            {
                var buffer = new char[512];
                while (true)
                {
                    var read = p.StandardOutput.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;
                    HandleChunk(new string(buffer, 0, read), stdout);
                }
            }
            catch (Exception ex) { lock (gate) readerFault ??= ex; }
        });

        var stderrTask = Task.Run(() =>
        {
            try
            {
                var buffer = new char[512];
                while (true)
                {
                    var read = p.StandardError.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;
                    HandleChunk(new string(buffer, 0, read), stderr);
                }
            }
            catch (Exception ex) { lock (gate) readerFault ??= ex; }
        });

        while (!p.WaitForExit(1500))
        {
            ThrowIfCancelled(request);
            if (!string.IsNullOrWhiteSpace(request.CancelFile) && File.Exists(request.CancelFile))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* */ }
                throw new OperationCanceledException();
            }

            var elapsed = DateTime.UtcNow - startedUtc;
            var time = elapsed.TotalHours >= 1
                ? elapsed.ToString(@"h\:mm\:ss")
                : elapsed.ToString(@"mm\:ss");
            string status;
            lock (gate) status = lastStatus;
            Progress(request, null, $"\u200B{status} · 경과 {time}");
        }

        Task.WaitAll(stdoutTask, stderrTask);
        if (readerFault is not null)
            throw new InvalidOperationException("프로세스 출력을 읽는 중 오류: " + readerFault.Message, readerFault);

        if (!ignoreExit && p.ExitCode != 0)
        {
            var detail = stderr.Length > 0 ? stderr.ToString() : stdout.ToString();
            throw new InvalidOperationException($"{Path.GetFileName(file)} 종료 코드 {p.ExitCode}. {detail.Trim()}");
        }
    }

    private static void RunPowerShell(string script, CustomIsoJobRequest request)
    {
        _ = RunPowerShellCapture(script, request);
    }

    private static string RunPowerShellCapture(string script, CustomIsoJobRequest request)
    {
        ThrowIfCancelled(request);
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

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("PowerShell 실행 실패");
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(600_000))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* */ }
            throw new TimeoutException("PowerShell 작업이 시간 초과되었습니다.");
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        if (p.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException(detail.Trim());
        }

        return stdout;
    }

    private static void Progress(CustomIsoJobRequest request, int? percent, string message)
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
            File.AppendAllText(request.ProgressFile,
                JsonSerializer.Serialize(line, WinCustomsJsonContext.Default.SystemImageProgressLine) + Environment.NewLine,
                Encoding.UTF8);
        }
        catch
        {
            // ignore
        }
    }

    private static void ThrowIfCancelled(CustomIsoJobRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.CancelFile) && File.Exists(request.CancelFile))
            throw new OperationCanceledException();
    }

    private static void ClearReadOnlyAttribute(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        }
        catch
        {
            // ignore — 마운트 단계에서 다시 실패 메시지로 드러난다.
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* */ }
    }
}
