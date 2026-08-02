using CommunityToolkit.Mvvm.ComponentModel;
using WinCustoms.Common;

namespace WinCustoms.Models;

public enum TweakCategory
{
    Explorer,
    Taskbar,
    Privacy,
    Performance,
    PowerTools
}

public enum TweakKind
{
    /// <summary>켜고 끌 수 있는 상태형 트윅. 적용/복원이 쌍으로 존재한다.</summary>
    Toggle,

    /// <summary>한 번 실행하는 동작형 트윅(임시 파일 삭제, 복원 지점 생성 등).</summary>
    Action
}

public enum TweakRisk
{
    /// <summary>되돌리기 쉬운 외형/편의 설정.</summary>
    Safe,

    /// <summary>시스템 동작에 영향을 주지만 복원 가능.</summary>
    Moderate,

    /// <summary>되돌리기 어렵거나 부작용이 있을 수 있음. 적용 전 확인이 필요하다.</summary>
    High
}

/// <summary>
/// 트윅 한 개. 모든 토글 트윅은 <see cref="ApplyAction"/> 과 <see cref="RestoreAction"/> 이
/// 반드시 쌍으로 존재해야 하며, <see cref="DetectApplied"/> 로 현재 시스템 상태를 읽어온다.
/// </summary>
public sealed partial class TweakItem : ObservableObject
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required TweakCategory Category { get; init; }

    public TweakKind Kind { get; init; } = TweakKind.Toggle;
    public TweakRisk Risk { get; init; } = TweakRisk.Safe;

    public bool RequiresAdmin { get; init; }
    public bool RequiresExplorerRestart { get; init; }
    public bool RequiresSignOut { get; init; }

    /// <summary>동작형 트윅의 버튼 문구.</summary>
    public string ActionText { get; init; } = "실행";

    /// <summary>추가 안내가 필요한 트윅의 참고 링크.</summary>
    public string? LearnMoreUrl { get; init; }

    /// <summary>적용 전에 백업(.reg)할 레지스트리 키 목록.</summary>
    public IReadOnlyList<(RegistryRoot Root, string SubKey)> BackupKeys { get; init; } = [];

    /// <summary>
    /// 오프라인 이미지(커스텀 ISO)에 이식할 레지스트리 적용 연산.
    /// null 이면 레지스트리로 표현되지 않는 트윅이라 ISO에 넣을 수 없다.
    /// </summary>
    public IReadOnlyList<RegistryOperation>? OfflineApplyOperations { get; init; }

    /// <summary>커스텀 ISO에 이식 가능한 레지스트리 토글인지.</summary>
    public bool SupportsOfflineImage => OfflineApplyOperations is { Count: > 0 };

    public required Func<CancellationToken, Task> ApplyAction { get; init; }

    /// <summary>동작형 트윅도 되돌릴 것이 없으면 완료된 Task 를 돌려주는 형태로 반드시 지정한다.</summary>
    public required Func<CancellationToken, Task> RestoreAction { get; init; }

    /// <summary>현재 시스템에 적용되어 있는지 판정. 동작형 트윅은 null 일 수 있다.</summary>
    public Func<bool>? DetectApplied { get; init; }

    // WinUI 3 + Native AOT 에서는 partial 프로퍼티를 써야 CsWinRT 가 WinRT 마샬링 코드를 생성한다.
    // 필드 기반 [ObservableProperty] 는 MVVMTK0045 경고와 함께 x:Bind 가 깨진다.

    /// <summary>실제 시스템 상태(읽기 전용 성격). 새로 고침으로만 갱신된다.</summary>
    [ObservableProperty]
    public partial bool IsApplied { get; set; }

    /// <summary>사용자가 토글로 지정한 "원하는 상태". 하단 [선택 항목 적용] 시 반영된다.</summary>
    [ObservableProperty]
    public partial bool IsRequested { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? LastError { get; set; }

    /// <summary>토글 위치가 실제 상태와 달라 아직 적용되지 않은 변경이 있는지.</summary>
    public bool IsDirty => Kind == TweakKind.Toggle && IsRequested != IsApplied;

    public bool IsToggle => Kind == TweakKind.Toggle;
    public bool IsAction => Kind == TweakKind.Action;

    public bool HasError => !string.IsNullOrEmpty(LastError);

    public bool HasBadges => RequiresAdmin || RequiresExplorerRestart || RequiresSignOut || Risk != TweakRisk.Safe;

    partial void OnIsRequestedChanged(bool value) => OnPropertyChanged(nameof(IsDirty));

    partial void OnIsAppliedChanged(bool value) => OnPropertyChanged(nameof(IsDirty));

    partial void OnLastErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    /// <summary>실제 상태를 다시 읽고 토글 위치를 그에 맞춰 되돌린다.</summary>
    public void RefreshState()
    {
        if (DetectApplied is null) return;

        var applied = false;
        try
        {
            applied = DetectApplied();
        }
        catch
        {
            // 상태를 읽지 못하면 "미적용"으로 간주한다.
        }

        IsApplied = applied;
        IsRequested = applied;
    }
}
