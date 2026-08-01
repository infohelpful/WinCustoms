using Microsoft.Win32;
using WinCustoms.Common;

namespace WinCustoms.Services;

/// <summary>레지스트리 읽기/쓰기/삭제와 백업을 담당한다.</summary>
public interface IRegistryService
{
    bool KeyExists(RegistryRoot root, string subKey);

    object? ReadValue(RegistryRoot root, string subKey, string name);

    int? ReadDword(RegistryRoot root, string subKey, string name);

    string? ReadString(RegistryRoot root, string subKey, string name);

    string[] GetSubKeyNames(RegistryRoot root, string subKey);

    string[] GetValueNames(RegistryRoot root, string subKey);

    /// <summary>지정한 스펙들이 모두 "적용된 값"과 일치하는지 확인한다.</summary>
    bool IsApplied(IReadOnlyList<RegistryValueSpec> specs);

    /// <summary>
    /// 연산 묶음을 실행한다. HKCU 는 즉시, HKLM/HKCR 은 필요 시 관리자 승격을 거쳐 처리한다.
    /// </summary>
    Task ExecuteAsync(IReadOnlyList<RegistryOperation> operations, CancellationToken ct = default);

    /// <summary>변경 전 상태를 .reg 파일로 내보낸다(수동 롤백용 안전장치).</summary>
    Task<string?> ExportBackupAsync(IEnumerable<(RegistryRoot Root, string SubKey)> keys, string label, CancellationToken ct = default);
}

/// <summary>승격이 필요한데 사용자가 UAC 를 거부했을 때 발생한다.</summary>
public sealed class ElevationDeniedException : Exception
{
    public ElevationDeniedException()
        : base("관리자 권한 승인이 취소되어 변경 사항을 적용하지 못했습니다.") { }
}

/// <summary>승격 작업 중 일부가 실패했을 때 발생한다.</summary>
public sealed class TweakOperationException(string message) : Exception(message);

internal static class RegistryRootExtensions
{
    public static RegistryKey Open(this RegistryRoot root) => root switch
    {
        RegistryRoot.CurrentUser => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default),
        RegistryRoot.LocalMachine => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
        RegistryRoot.ClassesRoot => RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Default),
        RegistryRoot.Users => RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default),
        _ => throw new ArgumentOutOfRangeException(nameof(root))
    };

    /// <summary>reg.exe / .reg 파일에서 쓰는 표기.</summary>
    public static string ToRegPrefix(this RegistryRoot root) => root switch
    {
        RegistryRoot.CurrentUser => "HKEY_CURRENT_USER",
        RegistryRoot.LocalMachine => "HKEY_LOCAL_MACHINE",
        RegistryRoot.ClassesRoot => "HKEY_CLASSES_ROOT",
        RegistryRoot.Users => "HKEY_USERS",
        _ => throw new ArgumentOutOfRangeException(nameof(root))
    };
}
