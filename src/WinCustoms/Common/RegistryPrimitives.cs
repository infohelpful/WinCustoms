using System.Globalization;
using Microsoft.Win32;

namespace WinCustoms.Common;

/// <summary>레지스트리 루트 하이브. 문자열 직렬화가 가능해야 승격 작업(job)으로 넘길 수 있다.</summary>
public enum RegistryRoot
{
    CurrentUser,
    LocalMachine,
    ClassesRoot,
    Users
}

public enum RegistryOperationKind
{
    SetValue,
    DeleteValue,
    CreateKey,
    DeleteKeyTree
}

/// <summary>
/// 단일 레지스트리 조작 단위. 관리자 권한이 필요한 경우 JSON 으로 직렬화되어
/// 승격된 자기 자신 프로세스로 전달된다.
/// </summary>
public sealed class RegistryOperation
{
    public RegistryOperationKind Kind { get; set; }
    public RegistryRoot Root { get; set; }
    public string SubKey { get; set; } = string.Empty;

    /// <summary>값 이름. 빈 문자열이면 기본값(Default).</summary>
    public string? Name { get; set; }

    public RegistryValueKind ValueKind { get; set; } = RegistryValueKind.String;

    /// <summary>문화권 독립적으로 직렬화된 값. 이진 값은 hex 문자열.</summary>
    public string? Value { get; set; }

    public static RegistryOperation Set(RegistryRoot root, string subKey, string name, RegistryValueKind kind, object value) => new()
    {
        Kind = RegistryOperationKind.SetValue,
        Root = root,
        SubKey = subKey,
        Name = name,
        ValueKind = kind,
        Value = RegistryValueCodec.Encode(kind, value)
    };

    public static RegistryOperation DeleteValue(RegistryRoot root, string subKey, string name) => new()
    {
        Kind = RegistryOperationKind.DeleteValue,
        Root = root,
        SubKey = subKey,
        Name = name
    };

    public static RegistryOperation CreateKey(RegistryRoot root, string subKey) => new()
    {
        Kind = RegistryOperationKind.CreateKey,
        Root = root,
        SubKey = subKey
    };

    public static RegistryOperation DeleteKeyTree(RegistryRoot root, string subKey) => new()
    {
        Kind = RegistryOperationKind.DeleteKeyTree,
        Root = root,
        SubKey = subKey
    };

    /// <summary>
    /// HKLM/HKCR/HKU 는 항상 승격.
    /// HKCU\Software\Policies 는 ACL/GPO 때문에 일반 권한으로 막히는 경우가 많아 승격한다
    /// (승격 시에는 원래 사용자 SID 하이브에 기록 — <see cref="ElevatedJob.TargetUserSid"/>).
    /// </summary>
    public bool RequiresElevation
    {
        get
        {
            if (Root is RegistryRoot.LocalMachine or RegistryRoot.ClassesRoot or RegistryRoot.Users)
                return true;

            if (Root is RegistryRoot.CurrentUser && IsUserPolicyPath(SubKey))
                return true;

            return false;
        }
    }

    private static bool IsUserPolicyPath(string subKey)
    {
        if (string.IsNullOrWhiteSpace(subKey)) return false;
        return subKey.StartsWith(@"Software\Policies\", StringComparison.OrdinalIgnoreCase)
               || subKey.Equals(@"Software\Policies", StringComparison.OrdinalIgnoreCase);
    }

    public override string ToString() => $"{Kind} {Root}\\{SubKey}" + (string.IsNullOrEmpty(Name) ? "" : $" [{Name}]");
}

/// <summary>레지스트리 값 ↔ 문자열 변환. AOT 안전(리플렉션 없음)하고 문화권 독립적이다.</summary>
public static class RegistryValueCodec
{
    public static string Encode(RegistryValueKind kind, object value) => kind switch
    {
        RegistryValueKind.DWord => Convert.ToInt32(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.Binary => Convert.ToHexString((byte[])value),
        RegistryValueKind.MultiString => string.Join('\u0000', (string[])value),
        _ => value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };

    public static object Decode(RegistryValueKind kind, string? encoded) => kind switch
    {
        RegistryValueKind.DWord => int.Parse(encoded ?? "0", CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => long.Parse(encoded ?? "0", CultureInfo.InvariantCulture),
        RegistryValueKind.Binary => string.IsNullOrEmpty(encoded) ? Array.Empty<byte>() : Convert.FromHexString(encoded),
        RegistryValueKind.MultiString => (encoded ?? string.Empty).Split('\u0000', StringSplitOptions.RemoveEmptyEntries),
        _ => encoded ?? string.Empty
    };

    /// <summary>레지스트리에서 읽어온 실제 값이 기대값과 같은지 타입에 맞게 비교한다.</summary>
    public static bool AreEqual(RegistryValueKind kind, object? actual, object expected)
    {
        if (actual is null) return false;

        return kind switch
        {
            RegistryValueKind.DWord => ToInt64(actual) == ToInt64(expected),
            RegistryValueKind.QWord => ToInt64(actual) == ToInt64(expected),
            RegistryValueKind.Binary => actual is byte[] a && expected is byte[] b && a.AsSpan().SequenceEqual(b),
            RegistryValueKind.MultiString => actual is string[] sa && expected is string[] sb && sa.SequenceEqual(sb, StringComparer.OrdinalIgnoreCase),
            _ => string.Equals(actual as string, expected as string, StringComparison.OrdinalIgnoreCase)
        };

        static long ToInt64(object o) => o switch
        {
            int i => i,
            long l => l,
            _ => Convert.ToInt64(o, CultureInfo.InvariantCulture)
        };
    }
}

/// <summary>
/// 트윅 하나가 조작하는 레지스트리 값의 "적용 상태"와 "기본 상태"를 함께 기술한다.
/// <see cref="DefaultValue"/> 가 null 이면 복원 시 값을 삭제한다(= 윈도우 기본 동작으로 되돌림).
/// </summary>
public sealed record RegistryValueSpec(
    RegistryRoot Root,
    string SubKey,
    string Name,
    RegistryValueKind Kind,
    object AppliedValue,
    object? DefaultValue = null)
{
    public static RegistryValueSpec Dword(RegistryRoot root, string subKey, string name, int applied, int? defaultValue = null)
        => new(root, subKey, name, RegistryValueKind.DWord, applied, defaultValue.HasValue ? (object?)defaultValue.Value : null);

    public static RegistryValueSpec Str(RegistryRoot root, string subKey, string name, string applied, string? defaultValue = null)
        => new(root, subKey, name, RegistryValueKind.String, applied, defaultValue);

    public RegistryOperation ToApplyOperation() => RegistryOperation.Set(Root, SubKey, Name, Kind, AppliedValue);

    public RegistryOperation ToRestoreOperation() => DefaultValue is null
        ? RegistryOperation.DeleteValue(Root, SubKey, Name)
        : RegistryOperation.Set(Root, SubKey, Name, Kind, DefaultValue);
}
