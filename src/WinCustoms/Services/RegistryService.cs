using System.Diagnostics;
using Microsoft.Win32;
using WinCustoms.Common;

namespace WinCustoms.Services;

/// <summary>
/// <see cref="IRegistryService"/> 기본 구현.
///
/// 설계 요점
///  - 읽기는 항상 현재 프로세스 권한으로 직접 수행한다(HKLM 읽기는 일반 사용자도 가능).
///  - 쓰기는 하이브별로 분리해, HKCU 는 즉시 처리하고
///    HKLM/HKCR/HKU 는 <see cref="IElevationService"/> 를 통해 승격 프로세스에 위임한다.
///    → UAC 프롬프트는 "적용" 한 번당 최대 1회만 뜬다.
/// </summary>
public sealed class RegistryService(IElevationService elevation) : IRegistryService
{
    private readonly IElevationService _elevation = elevation;

    public bool KeyExists(RegistryRoot root, string subKey)
    {
        try
        {
            using var baseKey = root.Open();
            using var key = baseKey.OpenSubKey(subKey);
            return key is not null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    public object? ReadValue(RegistryRoot root, string subKey, string name)
    {
        try
        {
            using var baseKey = root.Open();
            using var key = baseKey.OpenSubKey(subKey);
            return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    public int? ReadDword(RegistryRoot root, string subKey, string name)
        => ReadValue(root, subKey, name) is int i ? i : null;

    public string? ReadString(RegistryRoot root, string subKey, string name)
        => ReadValue(root, subKey, name) as string;

    public string[] GetSubKeyNames(RegistryRoot root, string subKey)
    {
        try
        {
            using var baseKey = root.Open();
            using var key = baseKey.OpenSubKey(subKey);
            return key?.GetSubKeyNames() ?? [];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return [];
        }
    }

    public string[] GetValueNames(RegistryRoot root, string subKey)
    {
        try
        {
            using var baseKey = root.Open();
            using var key = baseKey.OpenSubKey(subKey);
            return key?.GetValueNames() ?? [];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return [];
        }
    }

    public bool IsApplied(IReadOnlyList<RegistryValueSpec> specs)
    {
        if (specs.Count == 0) return false;

        foreach (var spec in specs)
        {
            var actual = ReadValue(spec.Root, spec.SubKey, spec.Name);
            if (!RegistryValueCodec.AreEqual(spec.Kind, actual, spec.AppliedValue))
                return false;
        }

        return true;
    }

    public async Task ExecuteAsync(IReadOnlyList<RegistryOperation> operations, CancellationToken ct = default)
    {
        if (operations.Count == 0) return;

        var direct = new List<RegistryOperation>();
        var elevated = new List<RegistryOperation>();
        var alreadyElevated = _elevation.IsElevated;

        foreach (var op in operations)
        {
            if (op.RequiresElevation && !alreadyElevated)
                elevated.Add(op);
            else
                direct.Add(op);
        }

        var errors = new List<string>();
        var retryElevated = new List<RegistryOperation>();

        foreach (var op in direct)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                ElevatedJobHost.ApplyRegistryOperation(op);
            }
            catch (Exception ex) when (
                !alreadyElevated
                && op.Root == RegistryRoot.CurrentUser
                && ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Policies ACL·잠긴 HKCU 값 등 — 승격 후 원래 사용자 SID 하이브에 재시도.
                retryElevated.Add(op);
            }
            catch (Exception ex)
            {
                errors.Add($"{op}: {ex.Message}");
            }
        }

        if (elevated.Count > 0 || retryElevated.Count > 0)
        {
            var job = new ElevatedJob
            {
                RegistryOperations = elevated.Concat(retryElevated).ToList()
            };
            var result = await _elevation.RunAsync(job, ct).ConfigureAwait(false);
            if (!result.Success)
                errors.AddRange(result.Errors);
        }

        if (errors.Count > 0)
            throw new TweakOperationException(string.Join(Environment.NewLine, errors));
    }

    public async Task<string?> ExportBackupAsync(
        IEnumerable<(RegistryRoot Root, string SubKey)> keys,
        string label,
        CancellationToken ct = default)
    {
        var targets = keys.Distinct().ToList();
        if (targets.Count == 0) return null;

        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinCustoms", "Backups");
        Directory.CreateDirectory(folder);

        var safeLabel = string.Join('_', label.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        var file = Path.Combine(folder, $"{DateTime.Now:yyyyMMdd-HHmmss}_{safeLabel}.reg");

        var merged = new List<string> { "Windows Registry Editor Version 5.00", string.Empty };
        var exportedAny = false;

        foreach (var (root, subKey) in targets)
        {
            ct.ThrowIfCancellationRequested();

            var fullPath = $@"{root.ToRegPrefix()}\{subKey}";
            var temp = Path.Combine(Path.GetTempPath(), $"wc-{Guid.NewGuid():N}.reg");

            try
            {
                // reg.exe export 는 존재하지 않는 키에 대해 실패하므로 조용히 건너뛴다.
                if (!KeyExists(root, subKey)) continue;

                var psi = new ProcessStartInfo("reg.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                ConsoleEncoding.ApplyTo(psi);
                psi.ArgumentList.Add("export");
                psi.ArgumentList.Add(fullPath);
                psi.ArgumentList.Add(temp);
                psi.ArgumentList.Add("/y");

                using (var process = Process.Start(psi))
                {
                    if (process is null) continue;
                    await process.WaitForExitAsync(ct).ConfigureAwait(false);
                    if (process.ExitCode != 0) continue;
                }

                var text = await File.ReadAllTextAsync(temp, ct).ConfigureAwait(false);
                // 각 파일의 헤더 줄은 제거하고 본문만 병합한다.
                var body = text.Replace("Windows Registry Editor Version 5.00", string.Empty).Trim();
                if (body.Length == 0) continue;

                merged.Add(body);
                merged.Add(string.Empty);
                exportedAny = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 백업 실패가 트윅 적용 자체를 막지는 않는다.
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* 정리 실패 무시 */ }
            }
        }

        if (!exportedAny) return null;

        await File.WriteAllLinesAsync(file, merged, ct).ConfigureAwait(false);
        return file;
    }
}
