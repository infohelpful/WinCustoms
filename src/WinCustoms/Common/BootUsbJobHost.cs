using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace WinCustoms.Common;

/// <summary>
/// Rufus 스타일: 순정/커스텀 미디어를 USB·외장 디스크에 기록.
/// 승격 프로세스에서 XAML 없이 실행한다.
/// </summary>
public static class BootUsbJobHost
{
    public const string JobSwitch = "--boot-usb-job";
    public const string Windows11DownloadUrl = "https://www.microsoft.com/software-download/windows11";

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

        BootUsbJobRequest? request = null;
        var result = new BootUsbJobResult();

        try
        {
            var json = File.ReadAllText(jobPath);
            request = JsonSerializer.Deserialize(json, WinCustomsJsonContext.Default.BootUsbJobRequest)
                      ?? throw new InvalidOperationException("부팅 USB 작업 파일을 해석할 수 없습니다.");

            Build(request);
            result.Success = true;
            result.TargetDescription = $"Disk {request.DiskNumber} · {request.DiskFriendlyName}";
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
            File.WriteAllText(resultPath, JsonSerializer.Serialize(result, WinCustomsJsonContext.Default.BootUsbJobResult));
        }
        catch
        {
            // ignore
        }
        finally
        {
            // 성공/실패/취소 모두 추출본 삭제 — C: 용량 회수
            if (!string.IsNullOrWhiteSpace(request?.WorkDirectory))
                WinCustomsWorkCleanup.TryDeleteTree(request.WorkDirectory);
        }

        return result.Success ? 0 : 1;
    }

    private sealed record PreparedUsbVolumes(string DataRoot, string? EfiRoot);

    private static void Build(BootUsbJobRequest request)
    {
        ThrowIfCancelled(request);
        ValidateRequest(request);

        var work = string.IsNullOrWhiteSpace(request.WorkDirectory)
            ? WinCustomsWorkCleanup.CreateJobWorkDirectory("BootUsb")
            : request.WorkDirectory;
        request.WorkDirectory = work;

        Progress(request, 3, "설치 미디어 준비(추출·커스터마이즈)...");
        var extractDir = CustomIsoJobHost.PrepareCustomizedMedia(ToIsoRequest(request));

        ThrowIfCancelled(request);
        Progress(request, 90, $"디스크 {request.DiskNumber} 초기화·포맷...");
        var volumes = PrepareTargetVolume(request);

        ThrowIfCancelled(request);
        MaybeSplitWimForFat32(extractDir, request);

        Progress(request, 92, "파일을 USB로 복사 중...");
        CopyMediaToVolumes(extractDir, volumes, request);

        if (request.CreateExtendedLabelAndIcon)
        {
            Progress(request, 97, "확장 레이블·아이콘(선택)...");
            try
            {
                WriteExtendedLabelAndIcon(volumes.DataRoot, request.VolumeLabel);
            }
            catch (Exception ex)
            {
                // Windows/Defender 가 USB 루트 autorun.inf 쓰기를 막는 경우가 많음.
                // 부팅 USB 본체와 무관하므로 실패해도 작성 성공으로 처리.
                Progress(request, 97, "확장 레이블 생략(권한 거부): " + TrimLog(ex.Message));
            }
        }

        if (request.PartitionScheme == BootUsbPartitionScheme.Mbr)
        {
            Progress(request, 98, "MBR 부팅 코드(bootsect)...");
            RunBootsect(volumes.DataRoot);
        }

        // 최종 검증: setup.exe 또는 sources 존재
        var setup = Path.Combine(volumes.DataRoot, "setup.exe");
        var sources = Path.Combine(volumes.DataRoot, "sources");
        if (!File.Exists(setup) && !Directory.Exists(sources))
            throw new InvalidOperationException(
                "복사 후 설치 파일을 찾지 못했습니다. USB 작성이 불완전합니다.");

        Progress(request, 99, "부팅 USB 작성 완료");
    }

    private static void ValidateRequest(BootUsbJobRequest request)
    {
        if (!File.Exists(request.SourceIsoPath))
            throw new FileNotFoundException("ISO를 찾을 수 없습니다.", request.SourceIsoPath);
        if (request.DiskNumber < 0)
            throw new InvalidOperationException("대상 디스크 번호가 올바르지 않습니다.");
        if (request.DiskSizeBytes > 0 && request.DiskSizeBytes < 4L * 1024 * 1024 * 1024)
            throw new InvalidOperationException("대상 디스크가 너무 작습니다 (최소 약 8GB 권장).");

        var safety = RunPowerShellCapture($$"""
            $d = Get-Disk -Number {{request.DiskNumber}} -ErrorAction Stop
            if ($null -eq $d) { 'MISSING' }
            elseif ($d.IsSystem -or $d.IsBoot) { 'SYSTEM' }
            else { 'OK' }
            """, timeoutMs: 60_000);
        if (safety.StartsWith("MISSING", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("대상 디스크를 찾을 수 없습니다.");
        if (safety.StartsWith("SYSTEM", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("시스템/부팅 디스크에는 작성할 수 없습니다.");
    }

    private static CustomIsoJobRequest ToIsoRequest(BootUsbJobRequest request) => new()
    {
        SourceIsoPath = request.SourceIsoPath,
        OutputIsoPath = Path.Combine(request.WorkDirectory, "_unused.iso"),
        ImageIndex = request.ImageIndex <= 0 ? 1 : request.ImageIndex,
        WorkDirectory = request.WorkDirectory,
        RegistryOperations = request.RegistryOperations,
        AppxPackageNames = request.AppxPackageNames,
        BypassSetupRequirements = request.BypassSetupRequirements,
        InjectHostDrivers = request.InjectHostDrivers,
        SkipOnlineAccount = request.SkipOnlineAccount,
        SkipPrivacyExperience = request.SkipPrivacyExperience,
        LocalAccountName = request.LocalAccountName ?? string.Empty,
        ProgressFile = request.ProgressFile,
        ResultFile = request.ResultFile + ".iso-phase",
        CancelFile = request.CancelFile
    };

    /// <summary>
    /// GPT+NTFS → EFI(FAT32 1GB) + 데이터(NTFS). UEFI 부팅 가능.
    /// 그 외 → 단일 파티션.
    /// 포맷 팝업 방지: ShellHWDetection 중지 + 문자 없이 포맷 후 할당.
    /// </summary>
    private static PreparedUsbVolumes PrepareTargetVolume(BootUsbJobRequest request)
    {
        var label = SanitizeLabel(request.VolumeLabel);
        var fs = request.FileSystem == BootUsbFileSystem.Ntfs ? "NTFS" : "FAT32";
        var style = request.PartitionScheme == BootUsbPartitionScheme.Gpt ? "GPT" : "MBR";
        var dual = request.PartitionScheme == BootUsbPartitionScheme.Gpt
                   && request.FileSystem == BootUsbFileSystem.Ntfs;
        var letter1 = FindFreeDriveLetter();
        var letter2 = FindFreeDriveLetter(exclude: letter1);
        var cluster = request.ClusterSizeBytes > 0 ? request.ClusterSizeBytes : 0;
        var quick = request.QuickFormat;

        // Clear-Disk 후에도 PartitionStyle 이 Raw 가 아닌 경우가 많아 Initialize-Disk 가
        // "already been initialized" 로 실패한다. diskpart clean+convert 로 확실히 초기화.
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $n = {{request.DiskNumber}}
            $want = '{{style}}'
            $fs = '{{fs}}'
            $label = '{{label.Replace("'", "''")}}'
            $dual = ${{dual.ToString().ToLowerInvariant()}}
            $quick = ${{quick.ToString().ToLowerInvariant()}}
            $cluster = {{cluster}}
            $L1 = '{{letter1}}'
            $L2 = '{{letter2}}'
            $restartHw = $false

            function Format-PartNoLetter($part, $fileSystem, $volLabel) {
              $a = @{
                Partition = $part
                FileSystem = $fileSystem
                NewFileSystemLabel = $volLabel
                Confirm = $false
                Force = $true
              }
              if (-not $quick) { $a['Full'] = $true }
              if ($cluster -gt 0 -and $fileSystem -eq 'NTFS') { $a['AllocationUnitSize'] = [int]$cluster }
              Format-Volume @a | Out-Null
              Start-Sleep -Milliseconds 500
              $v = Get-Partition -DiskNumber $part.DiskNumber -PartitionNumber $part.PartitionNumber | Get-Volume
              $t = [string]$v.FileSystemType
              if ($t -eq 'Unknown' -or [string]::IsNullOrWhiteSpace($t)) { throw "포맷 실패($fileSystem): RAW" }
              if ($fileSystem -eq 'NTFS' -and $t -ne 'NTFS') { throw "NTFS 포맷 실패: $t" }
              if ($fileSystem -eq 'FAT32' -and $t -ne 'FAT32') { throw "FAT32 포맷 실패: $t" }
            }

            function Assign-Letter($part, $preferred) {
              $order = New-Object System.Collections.Generic.List[char]
              [void]$order.Add([char]$preferred)
              for ($i = [int][char]'D'; $i -le [int][char]'Z'; $i++) {
                $ch = [char]$i
                if (-not $order.Contains($ch)) { [void]$order.Add($ch) }
              }
              foreach ($c in $order) {
                try {
                  Set-Partition -DiskNumber $part.DiskNumber -PartitionNumber $part.PartitionNumber -NewDriveLetter $c -ErrorAction Stop
                  return [string]$c
                } catch {}
              }
              throw '드라이브 문자 할당 실패'
            }

            function Wait-Root($root) {
              for ($i = 0; $i -lt 50; $i++) {
                if (Test-Path -LiteralPath $root) { return }
                Start-Sleep -Milliseconds 200
              }
              throw "경로 없음: $root"
            }

            function Reset-DiskStyle($diskNumber, $partitionStyle) {
              # 열려 있는 볼륨/문자부터 떼야 clean 이 안 터진다
              Get-Partition -DiskNumber $diskNumber -ErrorAction SilentlyContinue | ForEach-Object {
                $pn = $_.PartitionNumber
                if ($_.DriveLetter -and $_.DriveLetter -match '[A-Z]') {
                  try {
                    Remove-PartitionAccessPath -DiskNumber $diskNumber -PartitionNumber $pn -AccessPath ($_.DriveLetter + ':') -ErrorAction Stop
                  } catch {}
                }
                try {
                  Remove-Partition -DiskNumber $diskNumber -PartitionNumber $pn -Confirm:$false -ErrorAction Stop
                } catch {}
              }
              try { Set-Disk -Number $diskNumber -IsReadOnly $false } catch {}
              try { Set-Disk -Number $diskNumber -IsOffline $false } catch {}
              Start-Sleep -Milliseconds 400

              $convert = if ($partitionStyle -eq 'GPT') { 'convert gpt' } else { 'convert mbr' }
              # diskpart 는 앞 공백 있으면 0x80070057 (잘못된 매개 변수) 로 실패함
              $lines = @(
                "select disk $diskNumber"
                "online disk"
                "attributes disk clear readonly"
                "clean"
                $convert
              )
              $tmp = Join-Path $env:TEMP ('wc-dp-' + [guid]::NewGuid().ToString('N') + '.txt')
              [IO.File]::WriteAllLines($tmp, $lines, [Text.UTF8Encoding]::new($false))

              $log = ''
              $code = -1
              try {
                $psi = New-Object System.Diagnostics.ProcessStartInfo
                $psi.FileName = "$env:SystemRoot\System32\diskpart.exe"
                $psi.Arguments = '/s "' + $tmp + '"'
                $psi.UseShellExecute = $false
                $psi.RedirectStandardOutput = $true
                $psi.RedirectStandardError = $true
                $psi.CreateNoWindow = $true
                $proc = [Diagnostics.Process]::Start($psi)
                $log = $proc.StandardOutput.ReadToEnd() + $proc.StandardError.ReadToEnd()
                $proc.WaitForExit(180000) | Out-Null
                $code = $proc.ExitCode
              } finally {
                Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
              }

              Start-Sleep -Milliseconds 800
              $st = [string](Get-Disk -Number $diskNumber).PartitionStyle

              # exit code 가 HRESULT 로 나와도 스타일만 맞으면 성공
              if ($st -eq $partitionStyle) { return }

              if ($st -eq 'Raw' -or $st -eq 'Unknown') {
                Initialize-Disk -Number $diskNumber -PartitionStyle $partitionStyle -Confirm:$false
                Start-Sleep -Milliseconds 500
                $st = [string](Get-Disk -Number $diskNumber).PartitionStyle
                if ($st -eq $partitionStyle) { return }
              }

              # PowerShell 폴백
              try {
                Clear-Disk -Number $diskNumber -RemoveData -RemoveOEM -Confirm:$false -ErrorAction Stop
              } catch {}
              Start-Sleep -Milliseconds 600
              $st = [string](Get-Disk -Number $diskNumber).PartitionStyle
              if ($st -eq 'Raw' -or $st -eq 'Unknown') {
                Initialize-Disk -Number $diskNumber -PartitionStyle $partitionStyle -Confirm:$false
                Start-Sleep -Milliseconds 400
                $st = [string](Get-Disk -Number $diskNumber).PartitionStyle
              }
              if ($st -ne $partitionStyle) {
                $short = ($log -replace '\s+', ' ').Trim()
                if ($short.Length -gt 220) { $short = $short.Substring(0, 220) + '…' }
                throw ("디스크 초기화 실패 (diskpart=$code, style=$st): " + $short)
              }
            }

            try {
              try {
                $hw = Get-Service -Name 'ShellHWDetection' -ErrorAction SilentlyContinue
                if ($null -ne $hw -and $hw.Status -eq 'Running') {
                  try { Stop-Service -Name 'ShellHWDetection' -Force -ErrorAction Stop; $restartHw = $true } catch {}
                }

                $disk = Get-Disk -Number $n -ErrorAction Stop
                if ($disk.IsSystem -or $disk.IsBoot) { throw '시스템/부팅 디스크에는 작성할 수 없습니다.' }
                try { Set-Disk -Number $n -IsReadOnly $false } catch {}
                try { Set-Disk -Number $n -IsOffline $false } catch {}

                Reset-DiskStyle $n $want

                if ($dual) {
                  $efi = New-Partition -DiskNumber $n -Size 1024MB -GptType '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}'
                  Format-PartNoLetter $efi 'FAT32' 'ESP'
                  $data = New-Partition -DiskNumber $n -UseMaximumSize
                  Format-PartNoLetter $data 'NTFS' $label
                  $eL = Assign-Letter $efi $L2
                  $dL = Assign-Letter $data $L1
                  $eRoot = $eL + ':\'
                  $dRoot = $dL + ':\'
                  Wait-Root $eRoot
                  Wait-Root $dRoot
                  $p = Join-Path $dRoot ('wc-' + [guid]::NewGuid().ToString('N') + '.tmp')
                  [IO.File]::WriteAllText($p, 'ok'); Remove-Item $p -Force
                  Write-Output ("DATA=" + $dRoot)
                  Write-Output ("EFI=" + $eRoot)
                }
                else {
                  if ($want -eq 'MBR') { $part = New-Partition -DiskNumber $n -UseMaximumSize -IsActive }
                  else { $part = New-Partition -DiskNumber $n -UseMaximumSize }
                  Format-PartNoLetter $part $fs $label
                  $dL = Assign-Letter $part $L1
                  $dRoot = $dL + ':\'
                  Wait-Root $dRoot
                  $p = Join-Path $dRoot ('wc-' + [guid]::NewGuid().ToString('N') + '.tmp')
                  [IO.File]::WriteAllText($p, 'ok'); Remove-Item $p -Force
                  Write-Output ("DATA=" + $dRoot)
                }
              } catch {
                Write-Output ("ERR=" + $_.Exception.Message)
                exit 1
              }
            }
            finally {
              if ($restartHw) { try { Start-Service -Name 'ShellHWDetection' -ErrorAction SilentlyContinue } catch {} }
            }
            """;

        Progress(request, 90,
            dual
                ? $"디스크 {request.DiskNumber}: GPT · EFI(FAT32)+NTFS..."
                : $"디스크 {request.DiskNumber}: {style} · {fs}...");

        var output = RunPowerShellCapture(script, timeoutMs: 15 * 60 * 1000);
        string? data = null;
        string? efi = null;
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("DATA=", StringComparison.OrdinalIgnoreCase))
                data = line["DATA=".Length..].Trim();
            else if (line.StartsWith("EFI=", StringComparison.OrdinalIgnoreCase))
                efi = line["EFI=".Length..].Trim();
        }

        if (string.IsNullOrWhiteSpace(data))
            throw new InvalidOperationException("USB 포맷/파티션 준비 실패:\n" + output);

        if (!data.EndsWith('\\')) data += "\\";
        if (efi is not null && !efi.EndsWith('\\')) efi += "\\";
        if (!Directory.Exists(data))
            throw new InvalidOperationException("데이터 볼륨을 열 수 없습니다: " + data);
        if (efi is not null && !Directory.Exists(efi))
            throw new InvalidOperationException("EFI 볼륨을 열 수 없습니다: " + efi);

        Progress(request, 91, efi is null ? $"대상: {data}" : $"데이터 {data} · EFI {efi}");
        return new PreparedUsbVolumes(data, efi);
    }

    private static char FindFreeDriveLetter(char? exclude = null)
    {
        var used = new HashSet<char>();

        foreach (var d in DriveInfo.GetDrives())
        {
            if (!string.IsNullOrEmpty(d.Name))
                used.Add(char.ToUpperInvariant(d.Name[0]));
        }

        // 끊긴 네트워크 맵도 문자 점유 (HKCU\Network\Z 등)
        try
        {
            using var net = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Network");
            if (net is not null)
            {
                foreach (var name in net.GetSubKeyNames())
                {
                    if (name.Length == 1)
                        used.Add(char.ToUpperInvariant(name[0]));
                }
            }
        }
        catch { /* ignore */ }

        if (exclude is char ex)
            used.Add(char.ToUpperInvariant(ex));

        // 이미 쓰인 문자 다음 = D→Z 순으로 첫 빈 칸 (F까지 있으면 G)
        for (var c = 'D'; c <= 'Z'; c++)
        {
            if (!used.Contains(c))
                return c;
        }

        throw new InvalidOperationException("할당할 여유 드라이브 문자가 없습니다.");
    }

    private static string SanitizeLabel(string? label)
    {
        var s = (label ?? "WIN11").Trim();
        if (s.Length == 0) s = "WIN11";
        foreach (var ch in Path.GetInvalidFileNameChars().Concat(['"', '*', '?', '.', '/', '\\', '[', ']', ':', ';', '|', '=', ',']))
            s = s.Replace(ch, '_');
        if (s.Length > 11) s = s[..11];
        return s;
    }

    private static void MaybeSplitWimForFat32(string extractDir, BootUsbJobRequest request)
    {
        if (request.FileSystem != BootUsbFileSystem.Fat32)
            return;

        var wim = Path.Combine(extractDir, "sources", "install.wim");
        if (!File.Exists(wim))
            return;

        var info = new FileInfo(wim);
        const long limit = 4L * 1024 * 1024 * 1024 - 64 * 1024 * 1024;
        if (info.Length <= limit)
            return;

        Progress(request, 91, "FAT32용 install.wim 분할(SWM)...");
        ClearReadOnly(wim);
        var swm = Path.Combine(extractDir, "sources", "install.swm");
        RunProcess(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dism.exe"),
            [
                "/Split-Image",
                "/ImageFile:" + wim,
                "/SWMFile:" + swm,
                "/FileSize:4000"
            ],
            request);
        try { File.Delete(wim); } catch { /* */ }
    }

    private static void CopyMediaToVolumes(string extractDir, PreparedUsbVolumes volumes, BootUsbJobRequest request)
    {
        Robocopy(extractDir, volumes.DataRoot, request, "설치 파일");

        if (!string.IsNullOrWhiteSpace(volumes.EfiRoot))
        {
            Progress(request, 95, "EFI 파티션에 부팅 파일 복사...");
            // UEFI: ESP 에 efi\ + boot\ + sources\boot.wim
            foreach (var name in new[] { "efi", "boot" })
            {
                var src = Path.Combine(extractDir, name);
                if (Directory.Exists(src))
                    Robocopy(src, Path.Combine(volumes.EfiRoot, name), request, name);
            }

            var bootWim = Path.Combine(extractDir, "sources", "boot.wim");
            if (File.Exists(bootWim))
            {
                var destDir = Path.Combine(volumes.EfiRoot, "sources");
                Directory.CreateDirectory(destDir);
                var dest = Path.Combine(destDir, "boot.wim");
                ClearReadOnly(bootWim);
                File.Copy(bootWim, dest, overwrite: true);
            }

            // bootmgfw 경로 보강
            var bootx64 = Path.Combine(volumes.EfiRoot, "efi", "boot", "bootx64.efi");
            if (!File.Exists(bootx64))
            {
                var mgfw = Path.Combine(volumes.EfiRoot, "efi", "microsoft", "boot", "bootmgfw.efi");
                if (File.Exists(mgfw))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(bootx64)!);
                    File.Copy(mgfw, bootx64, overwrite: true);
                }
            }
        }

        Progress(request, 96, "파일 복사 완료");
    }

    private static void Robocopy(string source, string dest, BootUsbJobRequest request, string label)
    {
        Directory.CreateDirectory(dest);
        var psi = new ProcessStartInfo
        {
            FileName = "robocopy.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        ConsoleEncoding.ApplyTo(psi);
        foreach (var a in new[]
                 {
                     source.TrimEnd('\\'),
                     dest.TrimEnd('\\'),
                     "/E", "/COPY:DAT", "/R:2", "/W:2", "/NFL", "/NDL", "/NP"
                 })
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("robocopy 실행 실패");
        var output = new StringBuilder();
        while (!p.HasExited)
        {
            ThrowIfCancelled(request);
            var line = p.StandardOutput.ReadLine();
            if (line is not null)
            {
                output.AppendLine(line);
                Progress(request, null, "\u200B복사(" + label + ")…");
            }
            else Thread.Sleep(200);
        }

        output.Append(ConsoleEncoding.DecodeAuto(p.StandardOutput.ReadToEnd()));
        output.Append(ConsoleEncoding.DecodeAuto(p.StandardError.ReadToEnd()));
        if (p.ExitCode >= 8)
            throw new InvalidOperationException(
                $"파일 복사 실패({label}) 코드 {p.ExitCode}\n" + TrimLog(output.ToString()));
    }

    private static void WriteExtendedLabelAndIcon(string targetRoot, string volumeLabel)
    {
        var label = SanitizeLabel(volumeLabel);

        // 탐색기 볼륨 이름은 autorun 과 별개 — SetVolumeLabel 은 대부분 성공한다.
        try
        {
            var root = targetRoot.TrimEnd('\\') + "\\";
            if (!NativeMethods.SetVolumeLabel(root, label))
            {
                // 실패해도 계속 (레이블은 포맷 시 이미 넣었을 수 있음)
            }
        }
        catch { /* optional */ }

        var iconName = "WinCustoms.ico";
        var iconSrc = Path.Combine(AppContext.BaseDirectory, "Assets", "WinCustoms.ico");
        var iconDst = Path.Combine(targetRoot, iconName);
        var hasIcon = false;
        if (File.Exists(iconSrc))
        {
            try
            {
                if (File.Exists(iconDst))
                {
                    File.SetAttributes(iconDst, FileAttributes.Normal);
                    File.Delete(iconDst);
                }

                File.Copy(iconSrc, iconDst, overwrite: true);
                hasIcon = File.Exists(iconDst);
            }
            catch { /* optional */ }
        }

        var autorunPath = Path.Combine(targetRoot, "autorun.inf");
        var autorun = $"""
            [autorun]
            label={label}
            {(hasIcon ? $"icon={iconName}" : "")}
            """.Trim() + Environment.NewLine;

        // ISO 에서 온 autorun.inf 는 읽기 전용인 경우가 많음. Rufus 도 AV 가 막으면 이 파일만 스킵한다.
        ForceReplaceTextFile(autorunPath, autorun);
    }

    private static void ForceReplaceTextFile(string path, string contents)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch { /* recreate */ }

        using var fs = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            4096,
            FileOptions.WriteThrough);
        var bytes = Encoding.ASCII.GetBytes(contents);
        fs.Write(bytes, 0, bytes.Length);
        fs.Flush(flushToDisk: true);
    }

    private static void RunBootsect(string targetRoot)
    {
        var letter = targetRoot.TrimEnd('\\');
        var bootsect = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "bootsect.exe");
        if (!File.Exists(bootsect)) bootsect = "bootsect.exe";
        try
        {
            RunProcess(bootsect, ["/nt60", letter, "/force", "/mbr"], new BootUsbJobRequest(), ignoreExit: true);
        }
        catch { /* optional */ }
    }

    private static void ClearReadOnly(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        }
        catch { /* */ }
    }

    private static string TrimLog(string s)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length <= 160 ? s : s[..160] + "…";
    }

    private static void RunProcess(string file, IReadOnlyList<string> args, BootUsbJobRequest request, bool ignoreExit = false)
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
        var stdout = ConsoleEncoding.DecodeAuto(p.StandardOutput.ReadToEnd());
        var stderr = ConsoleEncoding.DecodeAuto(p.StandardError.ReadToEnd());
        p.WaitForExit();
        if (!ignoreExit && p.ExitCode != 0)
            throw new InvalidOperationException(
                $"{Path.GetFileName(file)} 종료 코드 {p.ExitCode}. {(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr)}".Trim());
    }

    private static string RunPowerShellCapture(string script, int timeoutMs = 600_000)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
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
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* */ }
            throw new TimeoutException("디스크 작업이 시간 초과되었습니다.");
        }

        var stdout = ConsoleEncoding.DecodeAuto(stdoutTask.GetAwaiter().GetResult() ?? string.Empty);
        var stderr = ConsoleEncoding.DecodeAuto(stderrTask.GetAwaiter().GetResult() ?? string.Empty);
        if (p.ExitCode != 0)
        {
            var errLine = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault(l => l.StartsWith("ERR=", StringComparison.OrdinalIgnoreCase));
            var detail = errLine is not null
                ? errLine["ERR=".Length..]
                : PreferPlainError(stdout, stderr);
            throw new InvalidOperationException(
                "디스크 준비 실패: " + (string.IsNullOrWhiteSpace(detail) ? $"종료 코드 {p.ExitCode}" : detail.Trim()));
        }

        return stdout.Trim();
    }

    private static string PreferPlainError(string stdout, string stderr)
    {
        static string StripCliXml(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            if (!s.Contains("CLIXML", StringComparison.OrdinalIgnoreCase))
                return s.Trim();

            // PowerShell 리다이렉트 시 stderr 가 CLIXML 로 옴 → 메시지 속성만 추출
            var msgs = new List<string>();
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(
                         s,
                         @"<S N=""Message"">(?<m>.*?)</S>",
                         System.Text.RegularExpressions.RegexOptions.Singleline))
            {
                var t = System.Net.WebUtility.HtmlDecode(m.Groups["m"].Value)
                    .Replace("_x000D__x000A_", " ")
                    .Replace("_x000A_", " ")
                    .Trim();
                if (!string.IsNullOrWhiteSpace(t))
                    msgs.Add(t);
            }

            if (msgs.Count > 0)
                return string.Join(" ", msgs.Distinct(StringComparer.OrdinalIgnoreCase));

            var idx = s.IndexOf("Initialize-Disk", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) idx = s.IndexOf("Exception", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var slice = s[idx..];
                return slice.Length <= 240 ? slice : slice[..240] + "…";
            }

            return "PowerShell 디스크 작업 실패";
        }

        var fromOut = StripCliXml(stdout);
        var fromErr = StripCliXml(stderr);
        if (!string.IsNullOrWhiteSpace(fromOut) && !fromOut.Contains("CLIXML", StringComparison.OrdinalIgnoreCase))
            return fromOut;
        if (!string.IsNullOrWhiteSpace(fromErr))
            return fromErr;
        return fromOut;
    }

    private static void Progress(BootUsbJobRequest request, int? percent, string message)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.ProgressFile)) return;
            var line = new SystemImageProgressLine { Percent = percent, Message = message };
            File.AppendAllText(
                request.ProgressFile,
                JsonSerializer.Serialize(line, WinCustomsJsonContext.Default.SystemImageProgressLine) + Environment.NewLine,
                Encoding.UTF8);
        }
        catch { /* */ }
    }

    private static void ThrowIfCancelled(BootUsbJobRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.CancelFile) && File.Exists(request.CancelFile))
            throw new OperationCanceledException();
    }

    public static IReadOnlyList<BootUsbDiskInfo> ListRemovableDisks()
    {
        var script = """
            Get-Disk | Where-Object {
              -not $_.IsSystem -and -not $_.IsBoot -and
              ($_.BusType -eq 'USB' -or $_.BusType -eq 'SD' -or $_.BusType -eq 'File Backed Virtual')
            } | ForEach-Object {
              $size = [int64]$_.Size
              '{0}|{1}|{2}|{3}|{4}' -f $_.Number, ($_.FriendlyName -replace '\|','/'), $size, $_.BusType, $_.PartitionStyle
            }
            """;

        var raw = RunPowerShellCapture(script, timeoutMs: 60_000);
        var list = new List<BootUsbDiskInfo>();
        if (string.IsNullOrWhiteSpace(raw)) return list;

        foreach (var line in raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('|');
            if (parts.Length < 4) continue;
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var num))
                continue;
            if (!long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
                size = 0;
            if (size < 64L * 1024 * 1024)
                continue;

            list.Add(new BootUsbDiskInfo
            {
                Number = num,
                FriendlyName = parts[1],
                SizeBytes = size,
                BusType = parts[3],
                PartitionStyle = parts.Length > 4 ? parts[4] : string.Empty
            });
        }

        return list.OrderBy(d => d.Number).ToList();
    }
}
