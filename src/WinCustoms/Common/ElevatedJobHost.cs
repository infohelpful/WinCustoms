using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace WinCustoms.Common;

/// <summary>
/// 승격된 프로세스 쪽 진입점. XAML 런타임을 초기화하지 않고 작업만 수행한 뒤 종료한다.
/// 덕분에 UAC 창은 한 번만 뜨고, 실패해도 UI 프로세스는 살아있다.
/// </summary>
public static class ElevatedJobHost
{
    public const string JobSwitch = "--elevated-job";

    public static bool IsJobInvocation(string[] args) => TryGetJobPath(args, out _);

    /// <summary>실행 파일 경로가 args[0] 에 포함되든 아니든 동작하도록 스위치를 스캔한다.</summary>
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

    /// <summary>승격 인스턴스에서 호출된다. 종료 코드 0 = 성공.</summary>
    public static int Run(string[] args)
    {
        if (!TryGetJobPath(args, out var jobPath))
            return 2;

        var resultPath = jobPath + ".result";
        var result = new ElevatedJobResult();

        try
        {
            var json = File.ReadAllText(jobPath);
            var job = JsonSerializer.Deserialize(json, WinCustomsJsonContext.Default.ElevatedJob)
                      ?? throw new InvalidOperationException("작업 파일을 해석할 수 없습니다.");

            Execute(job, result);
        }
        catch (Exception ex)
        {
            result.Errors.Add(ex.Message);
        }

        result.Success = result.Errors.Count == 0;

        try
        {
            File.WriteAllText(resultPath, JsonSerializer.Serialize(result, WinCustomsJsonContext.Default.ElevatedJobResult));
        }
        catch
        {
            // 결과 파일 기록 실패는 종료 코드로만 전달한다.
        }

        return result.Success ? 0 : 1;
    }

    /// <summary>이미 관리자 권한인 프로세스에서 직접 호출할 수도 있다.</summary>
    public static void Execute(ElevatedJob job, ElevatedJobResult result)
    {
        foreach (var op in job.RegistryOperations)
        {
            try
            {
                ApplyRegistryOperation(op);
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{op}: {ex.Message}");
            }
        }

        foreach (var cmd in job.Commands)
        {
            try
            {
                RunCommand(cmd);
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{cmd.FileName}: {ex.Message}");
            }
        }
    }

    public static void ApplyRegistryOperation(RegistryOperation op)
    {
        using var root = OpenRoot(op.Root);

        switch (op.Kind)
        {
            case RegistryOperationKind.CreateKey:
                root.CreateSubKey(op.SubKey, writable: true)?.Dispose();
                break;

            case RegistryOperationKind.SetValue:
            {
                using var key = root.CreateSubKey(op.SubKey, writable: true)
                                ?? throw new InvalidOperationException("키를 만들 수 없습니다.");
                key.SetValue(op.Name ?? string.Empty, RegistryValueCodec.Decode(op.ValueKind, op.Value), op.ValueKind);
                break;
            }

            case RegistryOperationKind.DeleteValue:
            {
                using var key = root.OpenSubKey(op.SubKey, writable: true);
                // 이미 없는 값을 지우는 것은 "기본 상태"라는 뜻이므로 성공으로 본다.
                key?.DeleteValue(op.Name ?? string.Empty, throwOnMissingValue: false);
                break;
            }

            case RegistryOperationKind.DeleteKeyTree:
                root.DeleteSubKeyTree(op.SubKey, throwOnMissingSubKey: false);
                break;
        }
    }

    private static RegistryKey OpenRoot(RegistryRoot root) => root switch
    {
        RegistryRoot.CurrentUser => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default),
        RegistryRoot.LocalMachine => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
        RegistryRoot.ClassesRoot => RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Default),
        RegistryRoot.Users => RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default),
        _ => throw new ArgumentOutOfRangeException(nameof(root))
    };

    private static void RunCommand(CommandOperation cmd)
    {
        var psi = new ProcessStartInfo
        {
            FileName = cmd.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var a in cmd.Arguments)
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException("프로세스를 시작할 수 없습니다.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(120_000);

        if (!cmd.IgnoreExitCode && process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException($"종료 코드 {process.ExitCode}. {detail.Trim()}");
        }
    }
}
