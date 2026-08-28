using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Win32;

namespace WinCustoms.Common;

/// <summary>
/// 마운트된 Windows 이미지에 레지스트리 연산을 오프라인으로 적용한다.
/// HKLM SOFTWARE/SYSTEM 하이브와 Default 사용자 NTUSER.DAT 를 로드한다.
/// </summary>
public static class OfflineRegistryApplier
{
    private const string SoftHive = "WC_SOFT";
    private const string SysHive = "WC_SYS";
    private const string UserHive = "WC_USER";

    public static void Apply(string mountDir, IReadOnlyList<RegistryOperation> operations, Action<string>? log = null)
    {
        if (operations.Count == 0) return;

        var softPath = Path.Combine(mountDir, "Windows", "System32", "config", "SOFTWARE");
        var sysPath = Path.Combine(mountDir, "Windows", "System32", "config", "SYSTEM");
        var userPath = Path.Combine(mountDir, "Users", "Default", "NTUSER.DAT");

        if (!File.Exists(softPath)) throw new FileNotFoundException("SOFTWARE 하이브를 찾을 수 없습니다.", softPath);
        if (!File.Exists(sysPath)) throw new FileNotFoundException("SYSTEM 하이브를 찾을 수 없습니다.", sysPath);
        if (!File.Exists(userPath)) throw new FileNotFoundException("Default NTUSER.DAT 를 찾을 수 없습니다.", userPath);

        var loadedSoft = false;
        var loadedSys = false;
        var loadedUser = false;
        var ok = 0;
        var fail = 0;

        try
        {
            RegLoad($"HKLM\\{SoftHive}", softPath);
            loadedSoft = true;
            RegLoad($"HKLM\\{SysHive}", sysPath);
            loadedSys = true;
            RegLoad($"HKLM\\{UserHive}", userPath);
            loadedUser = true;

            foreach (var op in operations)
            {
                try
                {
                    ApplyOne(op);
                    ok++;
                    log?.Invoke($"REG OK {op}");
                }
                catch (Exception ex)
                {
                    fail++;
                    log?.Invoke($"REG FAIL {op}: {ex.Message}");
                }
            }

            log?.Invoke($"REG 요약: 성공 {ok} · 실패 {fail}");
        }
        finally
        {
            // GC 로 핸들을 비운 뒤 unload (Win11 에서 잠금이 남는 경우 대비)
            GC.Collect();
            GC.WaitForPendingFinalizers();

            if (loadedUser) TryRegUnload($"HKLM\\{UserHive}");
            if (loadedSys) TryRegUnload($"HKLM\\{SysHive}");
            if (loadedSoft) TryRegUnload($"HKLM\\{SoftHive}");
        }
    }

    private static void ApplyOne(RegistryOperation op)
    {
        if (!TryMap(op, out var hiveRoot, out var subKey))
            throw new InvalidOperationException($"오프라인으로 매핑할 수 없는 키: {op.Root}\\{op.SubKey}");

        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

        switch (op.Kind)
        {
            case RegistryOperationKind.CreateKey:
            {
                using var _ = baseKey.CreateSubKey($"{hiveRoot}\\{subKey}", writable: true);
                break;
            }
            case RegistryOperationKind.SetValue:
            {
                using var key = baseKey.CreateSubKey($"{hiveRoot}\\{subKey}", writable: true)
                                ?? throw new InvalidOperationException("키를 만들 수 없습니다.");
                key.SetValue(op.Name ?? string.Empty, RegistryValueCodec.Decode(op.ValueKind, op.Value), op.ValueKind);
                break;
            }
            case RegistryOperationKind.DeleteValue:
            {
                using var key = baseKey.OpenSubKey($"{hiveRoot}\\{subKey}", writable: true);
                key?.DeleteValue(op.Name ?? string.Empty, throwOnMissingValue: false);
                break;
            }
            case RegistryOperationKind.DeleteKeyTree:
            {
                try
                {
                    baseKey.DeleteSubKeyTree($"{hiveRoot}\\{subKey}", throwOnMissingSubKey: false);
                }
                catch
                {
                    // ignore missing
                }

                break;
            }
        }
    }

    /// <summary>
    /// 라이브 루트/경로를 로드된 하이브 이름으로 변환한다.
    /// SOFTWARE 하이브 루트에는 'SOFTWARE\' 접두사가 없다.
    /// </summary>
    internal static bool TryMap(RegistryOperation op, out string hiveRoot, out string subKey)
    {
        hiveRoot = string.Empty;
        subKey = op.SubKey.TrimStart('\\');

        switch (op.Root)
        {
            case RegistryRoot.CurrentUser:
                hiveRoot = UserHive;
                return true;

            case RegistryRoot.LocalMachine:
            {
                if (subKey.StartsWith("SOFTWARE\\", StringComparison.OrdinalIgnoreCase))
                {
                    hiveRoot = SoftHive;
                    subKey = subKey["SOFTWARE\\".Length..];
                    return true;
                }

                if (subKey.Equals("SOFTWARE", StringComparison.OrdinalIgnoreCase))
                {
                    hiveRoot = SoftHive;
                    subKey = string.Empty;
                    return true;
                }

                if (subKey.StartsWith("SYSTEM\\", StringComparison.OrdinalIgnoreCase))
                {
                    hiveRoot = SysHive;
                    subKey = subKey["SYSTEM\\".Length..];
                    // 오프라인 이미지에는 CurrentControlSet 심볼릭 링크가 없을 수 있다.
                    if (subKey.StartsWith("CurrentControlSet\\", StringComparison.OrdinalIgnoreCase))
                        subKey = "ControlSet001\\" + subKey["CurrentControlSet\\".Length..];
                    else if (subKey.Equals("CurrentControlSet", StringComparison.OrdinalIgnoreCase))
                        subKey = "ControlSet001";
                    return true;
                }

                return false;
            }

            case RegistryRoot.ClassesRoot:
                // HKCR 은 보통 SOFTWARE\Classes
                hiveRoot = SoftHive;
                subKey = string.IsNullOrEmpty(subKey) ? "Classes" : "Classes\\" + subKey;
                return true;

            default:
                return false;
        }
    }

    private static void RegLoad(string keyName, string filePath)
    {
        var result = RunReg(["load", keyName, filePath]);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"reg load 실패 ({keyName}): {result.Combined}");
    }

    private static void TryRegUnload(string keyName)
    {
        for (var i = 0; i < 5; i++)
        {
            var result = RunReg(["unload", keyName]);
            if (result.ExitCode == 0) return;
            Thread.Sleep(400);
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private static (int ExitCode, string Combined) RunReg(IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "reg.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        ConsoleEncoding.ApplyOemTo(psi);
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("reg.exe 실행 실패");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        var combined = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        return (p.ExitCode, combined.Trim());
    }
}
