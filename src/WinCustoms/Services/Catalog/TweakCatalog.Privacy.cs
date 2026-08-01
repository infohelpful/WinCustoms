using WinCustoms.Common;
using WinCustoms.Models;

namespace WinCustoms.Services.Catalog;

public sealed partial class TweakCatalog
{
    private IReadOnlyList<TweakItem> BuildPrivacyTweaks() =>
    [
        _factory.FromRegistry(
            id: "privacy.telemetry",
            title: "진단 데이터 · 광고 ID 차단",
            description: "선택적 진단 데이터 전송과 광고 식별자를 끄고, 잠금 화면·설정 앱에 끼어드는 맞춤 추천을 비활성화합니다. "
                       + "Windows Update 같은 필수 통신은 그대로 유지됩니다.",
            category: TweakCategory.Privacy,
            specs:
            [
                // 0 = Security (Enterprise 전용), Home/Pro 에서는 사실상 '필수 데이터만'으로 동작한다.
                RegistryValueSpec.Dword(
                    RegistryRoot.LocalMachine, RegistryPaths.DataCollectionPolicy,
                    "AllowTelemetry", applied: 0),

                RegistryValueSpec.Dword(
                    RegistryRoot.LocalMachine, RegistryPaths.DataCollectionPolicy,
                    "DoNotShowFeedbackNotifications", applied: 1),

                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.AdvertisingInfo,
                    "Enabled", applied: 0, defaultValue: 1),

                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.PrivacyKey,
                    "TailoredExperiencesWithDiagnosticDataEnabled", applied: 0, defaultValue: 1),

                // 설정 앱 / 시작 메뉴 / 잠금 화면 제안 광고
                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ContentDeliveryManager,
                    "SilentInstalledAppsEnabled", applied: 0, defaultValue: 1),

                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ContentDeliveryManager,
                    "SystemPaneSuggestionsEnabled", applied: 0, defaultValue: 1),

                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ContentDeliveryManager,
                    "SubscribedContent-338388Enabled", applied: 0, defaultValue: 1),

                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ContentDeliveryManager,
                    "SubscribedContent-338389Enabled", applied: 0, defaultValue: 1),

                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ContentDeliveryManager,
                    "SubscribedContent-353694Enabled", applied: 0, defaultValue: 1)
            ],
            createKeysOnApply: [(RegistryRoot.LocalMachine, RegistryPaths.DataCollectionPolicy)],
            requiresSignOut: true,
            risk: TweakRisk.Moderate),

        _factory.FromRegistry(
            id: "privacy.disable-copilot",
            title: "Copilot 및 백그라운드 AI 기능 끄기",
            description: "Windows Copilot 과 Recall(회상)의 화면 분석 기능을 정책으로 비활성화합니다. "
                       + "AI 관련 백그라운드 프로세스가 뜨지 않아 유휴 메모리 사용량이 줄어듭니다.",
            category: TweakCategory.Privacy,
            specs:
            [
                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.CopilotPolicyUser,
                    "TurnOffWindowsCopilot", applied: 1),

                RegistryValueSpec.Dword(
                    RegistryRoot.LocalMachine, RegistryPaths.CopilotPolicyMachine,
                    "TurnOffWindowsCopilot", applied: 1),

                // Recall 스냅샷 저장 차단
                RegistryValueSpec.Dword(
                    RegistryRoot.LocalMachine, RegistryPaths.WindowsAiPolicy,
                    "DisableAIDataAnalysis", applied: 1),

                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ExplorerAdvanced,
                    "ShowCopilotButton", applied: 0, defaultValue: 1)
            ],
            createKeysOnApply:
            [
                (RegistryRoot.CurrentUser, RegistryPaths.CopilotPolicyUser),
                (RegistryRoot.LocalMachine, RegistryPaths.CopilotPolicyMachine),
                (RegistryRoot.LocalMachine, RegistryPaths.WindowsAiPolicy)
            ],
            requiresExplorerRestart: true,
            risk: TweakRisk.Moderate),

        _factory.FromRegistry(
            id: "privacy.edge-background",
            title: "Edge 백그라운드 상주 차단",
            description: "부팅 후 Microsoft Edge 가 창 없이 백그라운드에 머무는 동작과 시작 부스트를 끕니다. "
                       + "Edge 를 기본 브라우저로 쓰지 않는다면 체감 효과가 큽니다.",
            category: TweakCategory.Privacy,
            specs:
            [
                RegistryValueSpec.Dword(
                    RegistryRoot.LocalMachine, RegistryPaths.EdgePolicy,
                    "BackgroundModeEnabled", applied: 0),

                RegistryValueSpec.Dword(
                    RegistryRoot.LocalMachine, RegistryPaths.EdgePolicy,
                    "StartupBoostEnabled", applied: 0)
            ],
            createKeysOnApply: [(RegistryRoot.LocalMachine, RegistryPaths.EdgePolicy)]),

        BloatwareTweak()
    ];

    /// <summary>
    /// 기본 앱 일괄 삭제는 목록 선택 UI 가 필요하므로 전용 페이지로 안내한다.
    /// (실제 제거 로직은 <see cref="IAppxService"/> 에 있다.)
    /// </summary>
    private TweakItem BloatwareTweak() => new()
    {
        Id = "privacy.debloat",
        Title = "기본 앱(Bloatware) 정리",
        Description = "Xbox · Solitaire · 뉴스 · Teams 등 미리 설치된 앱을 골라 제거합니다. "
                    + "제거한 앱은 Microsoft Store 에서 다시 설치할 수 있지만 자동 복구는 되지 않으니 신중히 선택하세요.",
        Category = TweakCategory.Privacy,
        Kind = TweakKind.Action,
        ActionText = "앱 목록 열기",
        Risk = TweakRisk.High,
        DetectApplied = null,
        ApplyAction = _ => Task.CompletedTask,   // 실제 동작은 DebloatPage 로의 이동이며 ViewModel 이 처리한다.
        RestoreAction = _ => Task.CompletedTask
    };
}
