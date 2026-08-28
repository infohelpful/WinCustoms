using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace WinCustoms.Common;

/// <summary>
/// install.wim 오프라인 프로비저닝 AppX 제거 + 설치 완료 후 2차 정리 + 재설치 방지.
/// WinPE(boot.wim)가 아니라 마운트된 install.wim 에서 제거하는 것이 정석이다.
/// </summary>
internal static class ProvisionedAppxRemover
{
    public static void RemoveFromMountedImage(
        string mountDir,
        IReadOnlyList<string> names,
        Action<string>? log,
        Action? throwIfCancelled)
    {
        if (names.Count == 0) return;

        var wanted = names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (wanted.Count == 0) return;

        var mountEsc = mountDir.Replace("'", "''");
        var wantedLiteral = string.Join(", ", wanted.Select(PsQuote));

        var script = $$"""
            $ErrorActionPreference = 'Continue'
            $mount = '{{mountEsc}}'
            $wanted = @({{wantedLiteral}})

            function Test-Wanted([string]$Name) {
                if ([string]::IsNullOrWhiteSpace($Name)) { return $false }
                $family = ($Name -split '_', 2)[0]
                $cap = ($Name -split '~~~~', 2)[0]
                foreach ($w in $wanted) {
                    if ($Name -eq $w -or $family -eq $w -or $cap -eq $w) { return $true }
                    if ($Name.StartsWith($w + '_', [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
                    if ($Name.StartsWith($w + '~~~~', [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
                    if ($cap.Equals($w, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
                    if ($cap.EndsWith('.' + $w, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
                    if ($cap.StartsWith($w + '.', [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
                    if ($cap.StartsWith($w + '-', [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
                    if ($cap.EndsWith('-' + $w, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
                    if ($Name.Contains('.' + $w + '.', [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
                    if ($Name.EndsWith('.' + $w, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
                    if ($Name.StartsWith($w + '.', [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
                }
                return $false
            }

            $pass = 0
            do {
                $pass++
                $removed = 0
                $pkgs = @(Get-AppxProvisionedPackage -Path $mount | Where-Object { Test-Wanted $_.PackageName })
                foreach ($p in $pkgs) {
                    $name = $p.PackageName
                    try {
                        Remove-AppxProvisionedPackage -Path $mount -PackageName $name -ErrorAction Stop
                        Write-Output ('OK:' + $name)
                        $removed++
                    }
                    catch {
                        Write-Output ('PS_FAIL:' + $name + ':' + $_.Exception.Message)
                        & dism.exe /Image:$mount /Remove-ProvisionedAppxPackage /PackageName:$name 2>&1 | Out-Null
                        if ($LASTEXITCODE -eq 0) {
                            Write-Output ('DISM_OK:' + $name)
                            $removed++
                        }
                        else {
                            Write-Output ('DISM_FAIL:' + $name + ':exit=' + $LASTEXITCODE)
                        }
                    }
                }
                foreach ($c in @(Get-WindowsCapability -Path $mount -ErrorAction SilentlyContinue | Where-Object { $_.State -eq 'Installed' -and (Test-Wanted $_.Name) })) {
                    try {
                        Remove-WindowsCapability -Path $mount -Name $c.Name -ErrorAction Stop | Out-Null
                        Write-Output ('OK:capability:' + $c.Name)
                        $removed++
                    }
                    catch {
                        & dism.exe /Image:$mount /Remove-Capability /CapabilityName:$($c.Name) 2>&1 | Out-Null
                        if ($LASTEXITCODE -eq 0) {
                            Write-Output ('DISM_OK:capability:' + $c.Name)
                            $removed++
                        }
                    }
                }
                foreach ($f in @(Get-WindowsOptionalFeature -Path $mount -ErrorAction SilentlyContinue | Where-Object { $_.State -eq 'Enabled' -and (Test-Wanted $_.FeatureName) })) {
                    try {
                        Disable-WindowsOptionalFeature -Path $mount -FeatureName $f.FeatureName -NoRestart -ErrorAction Stop | Out-Null
                        Write-Output ('OK:feature:' + $f.FeatureName)
                        $removed++
                    }
                    catch {
                        & dism.exe /Image:$mount /Disable-Feature /FeatureName:$($f.FeatureName) /NoRestart 2>&1 | Out-Null
                        if ($LASTEXITCODE -eq 0) {
                            Write-Output ('DISM_OK:feature:' + $f.FeatureName)
                            $removed++
                        }
                    }
                }
            } while ($removed -gt 0 -and $pass -lt 6)

            Get-AppxProvisionedPackage -Path $mount |
              Where-Object { Test-Wanted $_.PackageName } |
              ForEach-Object { Write-Output ('LEFT:' + $_.PackageName) }
            """;

        throwIfCancelled?.Invoke();
        var output = RunPowerShell(script);
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            throwIfCancelled?.Invoke();
            if (line.StartsWith("OK:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("DISM_OK:", StringComparison.OrdinalIgnoreCase))
            {
                log?.Invoke("제거: " + line[(line.IndexOf(':') + 1)..]);
            }
            else if (line.StartsWith("PS_FAIL:", StringComparison.OrdinalIgnoreCase)
                     || line.StartsWith("DISM_FAIL:", StringComparison.OrdinalIgnoreCase)
                     || line.StartsWith("LEFT:", StringComparison.OrdinalIgnoreCase))
            {
                log?.Invoke("앱 제거: " + line);
            }
        }
    }

    public static List<RegistryOperation> BuildAntiReprovisionRegistryOps() =>
    [
        RegistryOperation.Set(
            RegistryRoot.LocalMachine,
            @"SOFTWARE\Policies\Microsoft\Windows\CloudContent",
            "DisableWindowsConsumerFeatures",
            RegistryValueKind.DWord,
            1),
        RegistryOperation.Set(
            RegistryRoot.CurrentUser,
            RegistryPaths.ContentDeliveryManager,
            "ContentDeliveryAllowed",
            RegistryValueKind.DWord,
            0),
        RegistryOperation.Set(
            RegistryRoot.CurrentUser,
            RegistryPaths.ContentDeliveryManager,
            "OemPreInstalledAppsEnabled",
            RegistryValueKind.DWord,
            0),
        RegistryOperation.Set(
            RegistryRoot.CurrentUser,
            RegistryPaths.ContentDeliveryManager,
            "PreInstalledAppsEnabled",
            RegistryValueKind.DWord,
            0),
        RegistryOperation.Set(
            RegistryRoot.CurrentUser,
            RegistryPaths.ContentDeliveryManager,
            "SilentInstalledAppsEnabled",
            RegistryValueKind.DWord,
            0),
        RegistryOperation.Set(
            RegistryRoot.CurrentUser,
            RegistryPaths.ContentDeliveryManager,
            "SubscribedContent-310093Enabled",
            RegistryValueKind.DWord,
            0),
        RegistryOperation.Set(
            RegistryRoot.CurrentUser,
            RegistryPaths.ContentDeliveryManager,
            "SubscribedContent-338388Enabled",
            RegistryValueKind.DWord,
            0),
        RegistryOperation.Set(
            RegistryRoot.CurrentUser,
            RegistryPaths.ContentDeliveryManager,
            "SystemPaneSuggestionsEnabled",
            RegistryValueKind.DWord,
            0)
    ];

    /// <summary>WinCustoms-RemoveApps.ps1 본문만 작성. SetupComplete.cmd 는 OemSetupScripts 가 만든다.</summary>
    public static void WriteDebloatScript(string scriptDir, IReadOnlyList<string> names)
    {
        var wanted = names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (wanted.Count == 0) return;

        Directory.CreateDirectory(scriptDir);
        var psPath = Path.Combine(scriptDir, "WinCustoms-RemoveApps.ps1");

        var wantedLiteral = string.Join(", ", wanted.Select(PsQuote));
        var ps = $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            $log = Join-Path $env:WINDIR 'Panther\WinCustoms-Debloat.log'
            $wanted = @({{wantedLiteral}})

            function Test-Wanted([string]$Name) {
                if ([string]::IsNullOrWhiteSpace($Name)) { return $false }
                $family = ($Name -split '_', 2)[0]
                $cap = ($Name -split '~~~~', 2)[0]
                foreach ($w in $wanted) {
                    if ($Name -eq $w -or $family -eq $w -or $cap -eq $w) { return $true }
                    if ($Name.StartsWith($w + '_', [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
                    if ($Name.StartsWith($w + '~~~~', [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
                    if ($cap.Equals($w, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
                    if ($cap.EndsWith('.' + $w, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
                    if ($cap.StartsWith($w + '.', [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
                    if ($cap.StartsWith($w + '-', [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
                    if ($cap.EndsWith('-' + $w, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
                    if ($Name.Contains('.' + $w + '.', [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
                    if ($Name.EndsWith('.' + $w, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
                    if ($Name.StartsWith($w + '.', [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
                }
                return $false
            }

            function Log([string]$msg) {
                $line = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + ' ' + $msg
                Add-Content -Path $log -Value $line
            }

            Log 'WinCustoms debloat start'
            $pass = 0
            do {
                $pass++
                $removed = 0
                foreach ($p in @(Get-AppxProvisionedPackage -Online | Where-Object { Test-Wanted $_.PackageName })) {
                    Remove-AppxProvisionedPackage -Online -PackageName $p.PackageName
                    Log ('provisioned removed: ' + $p.PackageName)
                    $removed++
                }
                foreach ($p in @(Get-AppxPackage -AllUsers | Where-Object { Test-Wanted $_.Name })) {
                    Remove-AppxPackage -Package $p.PackageFullName -AllUsers
                    Log ('package removed: ' + $p.Name)
                    $removed++
                }
                foreach ($c in @(Get-WindowsCapability -Online -ErrorAction SilentlyContinue | Where-Object { $_.State -eq 'Installed' -and (Test-Wanted $_.Name) })) {
                    Remove-WindowsCapability -Online -Name $c.Name -ErrorAction SilentlyContinue | Out-Null
                    Log ('capability removed: ' + $c.Name)
                    $removed++
                }
                foreach ($f in @(Get-WindowsOptionalFeature -Online -ErrorAction SilentlyContinue | Where-Object { $_.State -eq 'Enabled' -and (Test-Wanted $_.FeatureName) })) {
                    Disable-WindowsOptionalFeature -Online -FeatureName $f.FeatureName -NoRestart -ErrorAction SilentlyContinue | Out-Null
                    Log ('feature disabled: ' + $f.FeatureName)
                    $removed++
                }
            } while ($removed -gt 0 -and $pass -lt 4)

            Get-AppxProvisionedPackage -Online | Where-Object { Test-Wanted $_.PackageName } |
              ForEach-Object { Log ('provisioned left: ' + $_.PackageName) }
            Get-AppxPackage -AllUsers | Where-Object { Test-Wanted $_.Name } |
              ForEach-Object { Log ('package left: ' + $_.Name) }
            Log 'WinCustoms debloat done'
            """;

        File.WriteAllText(psPath, ps, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";

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

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("PowerShell 실행 실패");
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(600_000))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* */ }
            throw new TimeoutException("프로비저닝 앱 제거 PowerShell 이 시간 초과되었습니다.");
        }

        return ConsoleEncoding.DecodeAuto(stdoutTask.GetAwaiter().GetResult())
               + ConsoleEncoding.DecodeAuto(stderrTask.GetAwaiter().GetResult());
    }
}
