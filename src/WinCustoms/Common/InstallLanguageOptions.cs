namespace WinCustoms.Common;

/// <summary>
/// 커스텀 ISO/부팅 USB 만들기 화면의 "설치 언어" 드롭다운 옵션.
/// 표시 문자열 ↔ 로케일 코드 매핑은 <see cref="CustomIsoUnattend"/>의 MapLocale이
/// 실제로 지원하는 로케일과 반드시 일치해야 한다 — 여기 없는 코드를 override로 넘기면
/// MapLocale이 en-US로 떨어져 버린다.
/// </summary>
internal static class InstallLanguageOptions
{
    public const string Auto = "자동 (원본 ISO 언어 따라가기)";

    private static readonly (string Display, string LocaleCode)[] Map =
    [
        (Auto, string.Empty),
        ("한국어", "ko-KR"),
        ("English", "en-US"),
        ("日本語", "ja-JP"),
        ("简体中文", "zh-CN"),
        ("繁體中文", "zh-TW"),
    ];

    public static IReadOnlyList<string> DisplayNames { get; } = Map.Select(m => m.Display).ToList();

    /// <summary>표시 문자열 → 로케일 코드. "자동"이거나 못 찾으면 빈 문자열(자동 감지).</summary>
    public static string ToLocaleCode(string? display)
    {
        if (string.IsNullOrWhiteSpace(display))
            return string.Empty;

        foreach (var (d, code) in Map)
        {
            if (string.Equals(d, display, StringComparison.Ordinal))
                return code;
        }

        return string.Empty;
    }
}
