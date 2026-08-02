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
            description: "진단·광고 ID가 켜져 있으면 사용 기록이 맞춤 광고에 쓰입니다. 끄면 추적과 추천 광고가 줄어듭니다.",
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
                    RegistryRoot.LocalMachine, RegistryPaths.AdvertisingInfoPolicy,
                    "DisabledByGroupPolicy", applied: 1),

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
            createKeysOnApply:
            [
                (RegistryRoot.LocalMachine, RegistryPaths.DataCollectionPolicy),
                (RegistryRoot.LocalMachine, RegistryPaths.AdvertisingInfoPolicy)
            ],
            requiresSignOut: true,
            risk: TweakRisk.Moderate),

        _factory.FromRegistry(
            id: "privacy.tips-suggestions",
            title: "Windows 팁 · 추천 콘텐츠 · 소비자 체험 끄기",
            description: "팁·추천이 켜져 있으면 알림과 시작 메뉴에 광고성 제안이 뜹니다. 끄면 방해 없이 쓸 수 있습니다.",
            category: TweakCategory.Privacy,
            specs:
            [
                RegistryValueSpec.Dword(
                    RegistryRoot.LocalMachine, RegistryPaths.CloudContentPolicy,
                    "DisableWindowsConsumerFeatures", applied: 1),

                RegistryValueSpec.Dword(
                    RegistryRoot.LocalMachine, RegistryPaths.CloudContentPolicy,
                    "DisableSoftLanding", applied: 1),

                RegistryValueSpec.Dword(
                    RegistryRoot.LocalMachine, RegistryPaths.CloudContentPolicy,
                    "DisableCloudOptimizedContent", applied: 1),

                RegistryValueSpec.Dword(
                    RegistryRoot.LocalMachine, RegistryPaths.CloudContentPolicy,
                    "DisableConsumerAccountStateContent", applied: 1),

                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ContentDeliveryManager,
                    "SoftLandingEnabled", applied: 0, defaultValue: 1),

                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ContentDeliveryManager,
                    "SubscribedContentEnabled", applied: 0, defaultValue: 1),

                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ContentDeliveryManager,
                    "ContentDeliveryAllowed", applied: 0, defaultValue: 1),

                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ContentDeliveryManager,
                    "OemPreInstalledAppsEnabled", applied: 0, defaultValue: 1),

                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ContentDeliveryManager,
                    "PreInstalledAppsEnabled", applied: 0, defaultValue: 1),

                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ContentDeliveryManager,
                    "SubscribedContent-310093Enabled", applied: 0, defaultValue: 1),

                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ContentDeliveryManager,
                    "SubscribedContent-338393Enabled", applied: 0, defaultValue: 1),

                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ContentDeliveryManager,
                    "SubscribedContent-353698Enabled", applied: 0, defaultValue: 1)
            ],
            createKeysOnApply: [(RegistryRoot.LocalMachine, RegistryPaths.CloudContentPolicy)],
            requiresSignOut: true),

        _factory.FromRegistry(
            id: "privacy.lockscreen-spotlight",
            title: "잠금 화면 Spotlight · 광고 끄기",
            description: "Spotlight가 켜져 있으면 잠금 화면에 광고·추천 콘텐츠가 돌아갑니다. 끄면 내가 고른 화면만 남습니다.",
            category: TweakCategory.Privacy,
            specs:
            [
                RegistryValueSpec.Dword(
                    RegistryRoot.LocalMachine, RegistryPaths.CloudContentPolicy,
                    "DisableWindowsSpotlightFeatures", applied: 1),

                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.CloudContentPolicy,
                    "DisableWindowsSpotlightFeatures", applied: 1),

                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.CloudContentPolicy,
                    "DisableSpotlightCollectionOnDesktop", applied: 1),

                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ContentDeliveryManager,
                    "RotatingLockScreenEnabled", applied: 0, defaultValue: 1),

                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ContentDeliveryManager,
                    "RotatingLockScreenOverlayEnabled", applied: 0, defaultValue: 1)
            ],
            createKeysOnApply:
            [
                (RegistryRoot.LocalMachine, RegistryPaths.CloudContentPolicy),
                (RegistryRoot.CurrentUser, RegistryPaths.CloudContentPolicy)
            ],
            requiresSignOut: true),

        _factory.FromRegistry(
            id: "privacy.activity-history",
            title: "활동 기록 · Timeline 끄기",
            description: "활동 기록이 켜져 있으면 연 앱·파일이 쌓이고 동기화될 수 있습니다. 끄면 사용 흔적이 남지 않습니다.",
            category: TweakCategory.Privacy,
            specs:
            [
                RegistryValueSpec.Dword(
                    RegistryRoot.LocalMachine, RegistryPaths.SystemPolicy,
                    "EnableActivityFeed", applied: 0),

                RegistryValueSpec.Dword(
                    RegistryRoot.LocalMachine, RegistryPaths.SystemPolicy,
                    "PublishUserActivities", applied: 0),

                RegistryValueSpec.Dword(
                    RegistryRoot.LocalMachine, RegistryPaths.SystemPolicy,
                    "UploadUserActivities", applied: 0),

                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.PrivacyKey,
                    "PublishUserActivities", applied: 0, defaultValue: 1)
            ],
            createKeysOnApply: [(RegistryRoot.LocalMachine, RegistryPaths.SystemPolicy)]),

        _factory.FromRegistry(
            id: "privacy.location",
            title: "위치 서비스 끄기",
            description: "위치 서비스가 켜져 있으면 앱이 어디에 있는지 읽을 수 있습니다. 끄면 위치 추적이 멈춥니다.",
            category: TweakCategory.Privacy,
            specs:
            [
                RegistryValueSpec.Dword(
                    RegistryRoot.LocalMachine, RegistryPaths.LocationAndSensorsPolicy,
                    "DisableLocation", applied: 1),

                RegistryValueSpec.Dword(
                    RegistryRoot.LocalMachine, RegistryPaths.LocationAndSensorsPolicy,
                    "DisableLocationScripting", applied: 1)
            ],
            createKeysOnApply: [(RegistryRoot.LocalMachine, RegistryPaths.LocationAndSensorsPolicy)]),

        _factory.FromRegistry(
            id: "privacy.input-personalization",
            title: "입력 · 필기 개인화 끄기",
            description: "입력 개인화가 켜져 있으면 타이핑·필기 내용이 학습됩니다. 끄면 입력은 되고 학습만 멈춥니다.",
            category: TweakCategory.Privacy,
            specs:
            [
                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.InputPersonalization,
                    "RestrictImplicitInkCollection", applied: 1, defaultValue: 0),

                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.InputPersonalization,
                    "RestrictImplicitTextCollection", applied: 1, defaultValue: 0),

                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.TrainedDataStore,
                    "HarvestContacts", applied: 0, defaultValue: 1),

                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.PersonalizationSettings,
                    "AcceptedPrivacyPolicy", applied: 0, defaultValue: 1)
            ],
            createKeysOnApply:
            [
                (RegistryRoot.CurrentUser, RegistryPaths.InputPersonalization),
                (RegistryRoot.CurrentUser, RegistryPaths.TrainedDataStore),
                (RegistryRoot.CurrentUser, RegistryPaths.PersonalizationSettings)
            ]),

        _factory.FromRegistry(
            id: "privacy.clipboard",
            title: "클립보드 기록 · 클라우드 동기화 끄기",
            description: "클립보드 기록이 켜져 있으면 복사한 내용이 저장·동기화될 수 있습니다. 끄면 일반 복붙만 남습니다.",
            category: TweakCategory.Privacy,
            specs:
            [
                RegistryValueSpec.Dword(
                    RegistryRoot.LocalMachine, RegistryPaths.SystemPolicy,
                    "AllowClipboardHistory", applied: 0),

                RegistryValueSpec.Dword(
                    RegistryRoot.LocalMachine, RegistryPaths.SystemPolicy,
                    "AllowCrossDeviceClipboard", applied: 0),

                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ClipboardUser,
                    "EnableClipboardHistory", applied: 0, defaultValue: 0)
            ],
            createKeysOnApply:
            [
                (RegistryRoot.LocalMachine, RegistryPaths.SystemPolicy),
                (RegistryRoot.CurrentUser, RegistryPaths.ClipboardUser)
            ]),

        _factory.FromRegistry(
            id: "privacy.suggested-actions",
            title: "복사 시 '제안된 작업' 끄기",
            description: "제안된 작업이 켜져 있으면 복사할 때마다 팝업이 끼어듭니다. 끄면 복사만 깔끔하게 됩니다.",
            category: TweakCategory.Privacy,
            specs:
            [
                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.SmartClipboard,
                    "Disabled", applied: 1, defaultValue: 0)
            ],
            createKeysOnApply: [(RegistryRoot.CurrentUser, RegistryPaths.SmartClipboard)]),

        _factory.FromRegistry(
            id: "privacy.app-launch-tracking",
            title: "앱 시작 추적 끄기",
            description: "앱 시작 추적이 켜져 있으면 자주 쓴 앱 기록이 남습니다. 끄면 실행은 되고 추적만 멈춥니다.",
            category: TweakCategory.Privacy,
            specs:
            [
                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ExplorerAdvanced,
                    "Start_TrackProgs", applied: 0, defaultValue: 1)
            ],
            requiresExplorerRestart: true),

        _factory.FromRegistry(
            id: "privacy.online-speech",
            title: "온라인 음성 인식 끄기",
            description: "온라인 음성 인식이 켜져 있으면 음성이 클라우드로 올라갑니다. 끄면 음성 데이터가 전송되지 않습니다.",
            category: TweakCategory.Privacy,
            specs:
            [
                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.OnlineSpeechPrivacy,
                    "HasAccepted", applied: 0, defaultValue: 0)
            ],
            createKeysOnApply: [(RegistryRoot.CurrentUser, RegistryPaths.OnlineSpeechPrivacy)]),

        _factory.FromRegistry(
            id: "privacy.find-my-device",
            title: "'내 장치 찾기' 끄기",
            description: "내 장치 찾기가 켜져 있으면 기기 위치가 계정에 연결됩니다. 끄면 분실 추적은 못 하지만 위치 공유가 멈춥니다.",
            category: TweakCategory.Privacy,
            specs:
            [
                RegistryValueSpec.Dword(
                    RegistryRoot.LocalMachine, RegistryPaths.FindMyDevicePolicy,
                    "AllowFindMyDevice", applied: 0)
            ],
            createKeysOnApply: [(RegistryRoot.LocalMachine, RegistryPaths.FindMyDevicePolicy)]),

        _factory.FromRegistry(
            id: "privacy.disable-copilot",
            title: "Copilot 및 백그라운드 AI 기능 끄기",
            description: "Copilot·Recall이 켜져 있으면 화면·입력을 분석하고 메모리를 씁니다. 끄면 AI 상주가 사라져 가벼워집니다.",
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
            description: "Edge 상주가 켜져 있으면 안 써도 메모리를 잡아먹습니다. 끄면 Edge를 열 때만 올라와 유휴 자원이 남습니다.",
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
        Description = "기본 앱이 깔려 있으면 안 쓰는 앱이 자리와 자원을 차지합니다. 지우면 목록이 깔끔해지고 용량이 남습니다.",
        Category = TweakCategory.Privacy,
        Kind = TweakKind.Action,
        ActionText = "앱 목록 열기",
        Risk = TweakRisk.High,
        DetectApplied = null,
        ApplyAction = _ => Task.CompletedTask,   // 실제 동작은 DebloatPage 로의 이동이며 ViewModel 이 처리한다.
        RestoreAction = _ => Task.CompletedTask
    };
}
