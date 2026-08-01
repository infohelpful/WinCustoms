using Microsoft.UI.Xaml;
using WinCustoms.Models;

namespace WinCustoms.Views;

/// <summary>
/// x:Bind 함수 바인딩용 정적 헬퍼.
/// IValueConverter 대신 이 방식을 쓰면 리플렉션이 없어 Native AOT 에서 그대로 동작하고,
/// 컴파일 타임에 타입 검사도 받는다.
/// </summary>
public static class XamlHelpers
{
    public static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Hide(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility ShowIfText(string? value)
        => string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;

    public static bool Not(bool value) => !value;

    public static bool Neither(bool first, bool second) => !first && !second;

    public static bool IsPositive(int value) => value > 0;

    public static string RiskLabel(TweakRisk risk) => risk switch
    {
        TweakRisk.High => "주의 필요",
        TweakRisk.Moderate => "시스템 설정",
        _ => "안전"
    };

    public static string AppliedSummary(int applied, int total)
        => total == 0 ? string.Empty : $"{total}개 항목 중 {applied}개 적용됨";

    public static string PendingSummary(bool hasPending)
        => hasPending ? "적용하지 않은 변경 사항이 있습니다." : "모든 변경 사항이 반영되었습니다.";
}
