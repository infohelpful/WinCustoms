using System.Globalization;
using System.Text;
using Microsoft.Win32;

namespace WinCustoms.Common;

/// <summary>
/// install.wim 오프라인 적용 + 설치/첫 로그온 시 레지스트리 트윅 2차 적용 스크립트.
/// </summary>
internal static class RegistryTweakScriptWriter
{
    public static string BuildPowerShell(IReadOnlyList<RegistryOperation> operations)
    {
        if (operations.Count == 0)
            return string.Empty;

        var sb = new StringBuilder(8192);
        sb.AppendLine("param(");
        sb.AppendLine("  [ValidateSet('SetupComplete','FirstLogon')]");
        sb.AppendLine("  [string]$Mode = 'SetupComplete'");
        sb.AppendLine(")");
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine("$log = Join-Path $env:WINDIR 'Panther\\WinCustoms-Tweaks.log'");
        sb.AppendLine("function Log([string]$m) {");
        sb.AppendLine("  Add-Content -Path $log -Value ((Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + ' ' + $m)");
        sb.AppendLine("}");
        sb.AppendLine("function Ensure-Key([string]$Path) {");
        sb.AppendLine("  if (-not (Test-Path -LiteralPath $Path)) {");
        sb.AppendLine("    New-Item -Path $Path -Force | Out-Null");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        sb.AppendLine("function Get-TweakPath([string]$Root, [string]$SubKey) {");
        sb.AppendLine("  if ($Root.StartsWith('HKU\\')) { return ($Root + '\\' + $SubKey).Replace('\\\\', '\\') }");
        sb.AppendLine("  return ($Root + ':\\' + $SubKey).Replace('\\\\', '\\')");
        sb.AppendLine("}");
        sb.AppendLine("function Set-TweakValue([string]$Root, [string]$SubKey, [string]$Name, [string]$Type, [string]$Value) {");
        sb.AppendLine("  $path = Get-TweakPath $Root $SubKey");
        sb.AppendLine("  Ensure-Key $path");
        sb.AppendLine("  try {");
        sb.AppendLine("    switch ($Type) {");
        sb.AppendLine("      'DWord' { Set-ItemProperty -LiteralPath $path -Name $Name -Type DWord -Value ([int]$Value) -Force; break }");
        sb.AppendLine("      'QWord' { Set-ItemProperty -LiteralPath $path -Name $Name -Type QWord -Value ([long]$Value) -Force; break }");
        sb.AppendLine("      'ExpandString' { Set-ItemProperty -LiteralPath $path -Name $Name -Type ExpandString -Value $Value -Force; break }");
        sb.AppendLine("      default { Set-ItemProperty -LiteralPath $path -Name $Name -Type String -Value $Value -Force }");
        sb.AppendLine("    }");
        sb.AppendLine("    Log (\"OK \" + $Root + \"\\\" + $SubKey + \" [\" + $Name + \"]\")");
        sb.AppendLine("  } catch {");
        sb.AppendLine("    Log (\"FAIL \" + $Root + \"\\\" + $SubKey + \" [\" + $Name + \"]: \" + $_.Exception.Message)");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        sb.AppendLine("function Apply-DefaultUser([scriptblock]$Action) {");
        sb.AppendLine("  $hive = 'HKU\\WC_DEFAULT'");
        sb.AppendLine("  $dat = Join-Path $env:SystemDrive 'Users\\Default\\NTUSER.DAT'");
        sb.AppendLine("  if (-not (Test-Path -LiteralPath $dat)) { Log 'Default NTUSER missing'; return }");
        sb.AppendLine("  & reg.exe load $hive $dat 2>>$log | Out-Null");
        sb.AppendLine("  try { & $Action $hive } finally { & reg.exe unload $hive 2>>$log | Out-Null }");
        sb.AppendLine("}");
        sb.AppendLine("Log (\"WinCustoms tweaks start (\" + $Mode + \")\")");

        foreach (var op in operations)
            AppendOperation(sb, op);

        sb.AppendLine("Log (\"WinCustoms tweaks done (\" + $Mode + \")\")");
        return sb.ToString();
    }

    private static void AppendOperation(StringBuilder sb, RegistryOperation op)
    {
        switch (op.Kind)
        {
            case RegistryOperationKind.SetValue:
                AppendSetValue(sb, op);
                break;
            case RegistryOperationKind.CreateKey:
                AppendCreateKey(sb, op);
                break;
            case RegistryOperationKind.DeleteValue:
                AppendDeleteValue(sb, op);
                break;
            case RegistryOperationKind.DeleteKeyTree:
                AppendDeleteKeyTree(sb, op);
                break;
        }
    }

    private static void AppendSetValue(StringBuilder sb, RegistryOperation op)
    {
        var name = PsQuote(op.Name ?? string.Empty);
        var subKey = PsQuote(NormalizeSubKey(op.SubKey));
        var (psType, psValue) = FormatPsValue(op.ValueKind, op.Value);

        switch (op.Root)
        {
            case RegistryRoot.CurrentUser:
                sb.AppendLine($"if ($Mode -eq 'FirstLogon') {{ Set-TweakValue -Root 'HKCU' -SubKey {subKey} -Name {name} -Type {psType} -Value {psValue} }}");
                sb.AppendLine($"Apply-DefaultUser {{ param($h) Set-TweakValue -Root $h -SubKey {subKey} -Name {name} -Type {psType} -Value {psValue} }}");
                break;
            case RegistryRoot.LocalMachine:
                sb.AppendLine($"Set-TweakValue -Root 'HKLM' -SubKey {subKey} -Name {name} -Type {psType} -Value {psValue}");
                break;
            case RegistryRoot.ClassesRoot:
                var userClass = PsQuote(@"Software\Classes\" + NormalizeSubKey(op.SubKey));
                var machineClass = PsQuote(@"SOFTWARE\Classes\" + NormalizeSubKey(op.SubKey));
                sb.AppendLine($"if ($Mode -eq 'FirstLogon') {{ Set-TweakValue -Root 'HKCU' -SubKey {userClass} -Name {name} -Type {psType} -Value {psValue} }}");
                sb.AppendLine($"Set-TweakValue -Root 'HKLM' -SubKey {machineClass} -Name {name} -Type {psType} -Value {psValue}");
                break;
        }
    }

    private static void AppendCreateKey(StringBuilder sb, RegistryOperation op)
    {
        var subKey = PsQuote(NormalizeSubKey(op.SubKey));
        switch (op.Root)
        {
            case RegistryRoot.CurrentUser:
                sb.AppendLine($"if ($Mode -eq 'FirstLogon') {{ Ensure-Key (Get-TweakPath 'HKCU' {subKey}) }}");
                sb.AppendLine($"Apply-DefaultUser {{ param($h) Ensure-Key (Get-TweakPath $h {subKey}) }}");
                break;
            case RegistryRoot.LocalMachine:
                sb.AppendLine($"Ensure-Key (Get-TweakPath 'HKLM' {subKey})");
                break;
            case RegistryRoot.ClassesRoot:
                sb.AppendLine($"if ($Mode -eq 'FirstLogon') {{ Ensure-Key (Get-TweakPath 'HKCU' {PsQuote(@"Software\Classes\" + NormalizeSubKey(op.SubKey))}) }}");
                sb.AppendLine($"Ensure-Key (Get-TweakPath 'HKLM' {PsQuote(@"SOFTWARE\Classes\" + NormalizeSubKey(op.SubKey))})");
                break;
        }
    }

    private static void AppendDeleteValue(StringBuilder sb, RegistryOperation op)
    {
        var name = PsQuote(op.Name ?? string.Empty);
        var subKey = PsQuote(NormalizeSubKey(op.SubKey));
        switch (op.Root)
        {
            case RegistryRoot.CurrentUser:
                sb.AppendLine($"if ($Mode -eq 'FirstLogon') {{ Remove-ItemProperty -LiteralPath (Get-TweakPath 'HKCU' {subKey}) -Name {name} -ErrorAction SilentlyContinue }}");
                sb.AppendLine($"Apply-DefaultUser {{ param($h) Remove-ItemProperty -LiteralPath (Get-TweakPath $h {subKey}) -Name {name} -ErrorAction SilentlyContinue }}");
                break;
            case RegistryRoot.LocalMachine:
                sb.AppendLine($"Remove-ItemProperty -LiteralPath (Get-TweakPath 'HKLM' {subKey}) -Name {name} -ErrorAction SilentlyContinue");
                break;
            case RegistryRoot.ClassesRoot:
                sb.AppendLine($"if ($Mode -eq 'FirstLogon') {{ Remove-ItemProperty -LiteralPath (Get-TweakPath 'HKCU' {PsQuote(@"Software\Classes\" + NormalizeSubKey(op.SubKey))}) -Name {name} -ErrorAction SilentlyContinue }}");
                sb.AppendLine($"Remove-ItemProperty -LiteralPath (Get-TweakPath 'HKLM' {PsQuote(@"SOFTWARE\Classes\" + NormalizeSubKey(op.SubKey))}) -Name {name} -ErrorAction SilentlyContinue");
                break;
        }
    }

    private static void AppendDeleteKeyTree(StringBuilder sb, RegistryOperation op)
    {
        var subKey = PsQuote(NormalizeSubKey(op.SubKey));
        switch (op.Root)
        {
            case RegistryRoot.CurrentUser:
                sb.AppendLine($"if ($Mode -eq 'FirstLogon') {{ Remove-Item -LiteralPath (Get-TweakPath 'HKCU' {subKey}) -Recurse -Force -ErrorAction SilentlyContinue }}");
                sb.AppendLine($"Apply-DefaultUser {{ param($h) Remove-Item -LiteralPath (Get-TweakPath $h {subKey}) -Recurse -Force -ErrorAction SilentlyContinue }}");
                break;
            case RegistryRoot.LocalMachine:
                sb.AppendLine($"Remove-Item -LiteralPath (Get-TweakPath 'HKLM' {subKey}) -Recurse -Force -ErrorAction SilentlyContinue");
                break;
            case RegistryRoot.ClassesRoot:
                sb.AppendLine($"if ($Mode -eq 'FirstLogon') {{ Remove-Item -LiteralPath (Get-TweakPath 'HKCU' {PsQuote(@"Software\Classes\" + NormalizeSubKey(op.SubKey))}) -Recurse -Force -ErrorAction SilentlyContinue }}");
                sb.AppendLine($"Remove-Item -LiteralPath (Get-TweakPath 'HKLM' {PsQuote(@"SOFTWARE\Classes\" + NormalizeSubKey(op.SubKey))}) -Recurse -Force -ErrorAction SilentlyContinue");
                break;
        }
    }

    private static string NormalizeSubKey(string subKey) => subKey.TrimStart('\\');

    private static (string Type, string Value) FormatPsValue(RegistryValueKind kind, string? encoded)
    {
        var decoded = RegistryValueCodec.Decode(kind, encoded);
        return kind switch
        {
            RegistryValueKind.DWord => ("'DWord'", Convert.ToInt32(decoded, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)),
            RegistryValueKind.QWord => ("'QWord'", Convert.ToInt64(decoded, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)),
            RegistryValueKind.ExpandString => ("'ExpandString'", PsQuote(decoded.ToString() ?? string.Empty)),
            _ => ("'String'", PsQuote(decoded.ToString() ?? string.Empty))
        };
    }

    private static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";
}
