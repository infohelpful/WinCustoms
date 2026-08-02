using Microsoft.Win32;
using WinCustoms.Common;
using WinCustoms.Models;

namespace WinCustoms.Services.Catalog;

public sealed partial class TweakCatalog
{
    private IReadOnlyList<TweakItem> BuildPerformanceTweaks() =>
    [
        _factory.Custom(
            id: "perf.ultimate-power-plan",
            title: "'최고의 성능' 전원 관리 옵션 사용",
            description: "균형 전원은 CPU를 아끼려 반응이 느릴 수 있습니다. 켜면 지연이 줄지만 전기·발열이 늘 수 있습니다.",
            category: TweakCategory.Performance,
            apply: ct => _maintenance.EnableUltimatePerformanceAsync(ct),
            restore: ct => _maintenance.DisableUltimatePerformanceAsync(ct),
            detect: () => _maintenance.IsUltimatePerformanceActive(),
            requiresAdmin: true,
            risk: TweakRisk.Moderate),

        _factory.FromRegistry(
            id: "perf.disable-animations",
            title: "창 애니메이션 · 시각 효과 끄기",
            description: "애니메이션·투명 효과가 켜져 있으면 클릭 반응이 무겁습니다. 끄면 화면이 단순해지고 반응이 빨라집니다.",
            category: TweakCategory.Performance,
            specs:
            [
                // 2 = 최적 성능으로 조정
                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ExplorerVisualEffects,
                    "VisualFXSetting", applied: 2, defaultValue: 0),

                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ExplorerAdvanced,
                    "TaskbarAnimations", applied: 0, defaultValue: 1),

                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ExplorerAdvanced,
                    "ListviewAlphaSelect", applied: 0, defaultValue: 1),

                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.DwmKey,
                    "EnableAeroPeek", applied: 0, defaultValue: 1),

                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.PersonalizeKey,
                    "EnableTransparency", applied: 0, defaultValue: 1),

                new RegistryValueSpec(
                    RegistryRoot.CurrentUser, RegistryPaths.DesktopKey,
                    "MenuShowDelay", RegistryValueKind.String,
                    AppliedValue: "0", DefaultValue: "400"),

                new RegistryValueSpec(
                    RegistryRoot.CurrentUser, RegistryPaths.DesktopKey,
                    "DragFullWindows", RegistryValueKind.String,
                    AppliedValue: "0", DefaultValue: "1"),

                // 최소화/최대화 애니메이션
                new RegistryValueSpec(
                    RegistryRoot.CurrentUser, $@"{RegistryPaths.WindowMetrics}",
                    "MinAnimate", RegistryValueKind.String,
                    AppliedValue: "0", DefaultValue: "1")
            ],
            requiresExplorerRestart: true),

        _factory.FromRegistry(
            id: "perf.block-driver-updates",
            title: "드라이버 자동 업데이트 방지",
            description: "드라이버 자동 업데이트가 켜져 있으면 안정된 드라이버가 덮어씌워질 수 있습니다. 끄면 내가 깔아 둔 드라이버가 유지됩니다.",
            category: TweakCategory.Performance,
            specs:
            [
                // 0 = 자동 드라이버 검색 안 함
                RegistryValueSpec.Dword(
                    RegistryRoot.LocalMachine, RegistryPaths.DriverSearching,
                    "SearchOrderConfig", applied: 0, defaultValue: 1),

                RegistryValueSpec.Dword(
                    RegistryRoot.LocalMachine, RegistryPaths.WindowsUpdatePolicy,
                    "ExcludeWUDriversInQualityUpdate", applied: 1),

                RegistryValueSpec.Dword(
                    RegistryRoot.LocalMachine, RegistryPaths.DeviceMetadata,
                    "PreventDeviceMetadataFromNetwork", applied: 1)
            ],
            createKeysOnApply:
            [
                (RegistryRoot.LocalMachine, RegistryPaths.WindowsUpdatePolicy),
                (RegistryRoot.LocalMachine, RegistryPaths.DeviceMetadata)
            ],
            risk: TweakRisk.Moderate),

        _factory.FromRegistry(
            id: "perf.faster-shutdown",
            title: "종료 대기 시간 단축",
            description: "기본 대기면 종료·재부팅이 길게 늘어집니다. 켜면 빨리 꺼지지만 저장 안 한 작업은 날아갈 수 있습니다.",
            category: TweakCategory.Performance,
            specs:
            [
                new RegistryValueSpec(
                    RegistryRoot.CurrentUser, RegistryPaths.DesktopKey,
                    "HungAppTimeout", RegistryValueKind.String,
                    AppliedValue: "2000", DefaultValue: "5000"),

                new RegistryValueSpec(
                    RegistryRoot.CurrentUser, RegistryPaths.DesktopKey,
                    "WaitToKillAppTimeout", RegistryValueKind.String,
                    AppliedValue: "3000", DefaultValue: "20000"),

                new RegistryValueSpec(
                    RegistryRoot.CurrentUser, RegistryPaths.DesktopKey,
                    "AutoEndTasks", RegistryValueKind.String,
                    AppliedValue: "1", DefaultValue: "0")
            ],
            requiresSignOut: true,
            risk: TweakRisk.Moderate),

        _factory.FromRegistry(
            id: "perf.disable-delivery-optimization",
            title: "다운로드 최적화(Delivery Optimization) 끄기",
            description: "다운로드 최적화가 켜져 있으면 다른 PC와 업데이트를 공유해 네트워크·디스크가 바쁩니다. 끄면 공유만 멈추고 업데이트는 됩니다.",
            category: TweakCategory.Performance,
            specs:
            [
                // 0 = HTTP 전용(피어 공유 없음). 정책 값을 지우면 OS 기본으로 돌아간다.
                RegistryValueSpec.Dword(
                    RegistryRoot.LocalMachine, RegistryPaths.DeliveryOptimizationPolicy,
                    "DODownloadMode", applied: 0)
            ],
            createKeysOnApply: [(RegistryRoot.LocalMachine, RegistryPaths.DeliveryOptimizationPolicy)]),

        _factory.FromRegistry(
            id: "perf.disable-game-dvr",
            title: "Xbox Game Bar · 게임 DVR 끄기",
            description: "Game Bar·DVR이 켜져 있으면 백그라운드에서 GPU·디스크를 씁니다. 끄면 안 쓰는 녹화 부담이 사라집니다.",
            category: TweakCategory.Performance,
            specs:
            [
                RegistryValueSpec.Dword(
                    RegistryRoot.LocalMachine, RegistryPaths.GameDvrPolicy,
                    "AllowGameDVR", applied: 0),

                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.GameDvrUser,
                    "AppCaptureEnabled", applied: 0, defaultValue: 1),

                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.GameConfigStore,
                    "GameDVR_Enabled", applied: 0, defaultValue: 1)
            ],
            createKeysOnApply: [(RegistryRoot.LocalMachine, RegistryPaths.GameDvrPolicy)]),

        _factory.FromRegistry(
            id: "perf.disable-background-apps",
            title: "백그라운드 앱 실행 제한",
            description: "백그라운드 앱이 켜져 있으면 안 쓰는 앱도 계속 돕니다. 끄면 유휴 CPU·배터리가 덜 나갑니다.",
            category: TweakCategory.Performance,
            specs:
            [
                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.BackgroundAccessApplications,
                    "GlobalUserDisabled", applied: 1, defaultValue: 0)
            ],
            requiresSignOut: true),

        _factory.FromRegistry(
            id: "perf.ntfs-disk-tweaks",
            title: "NTFS 마지막 액세스 기록 · 8.3 이름 끄기",
            description: "NTFS 기록이 켜져 있으면 파일 열 때마다 디스크에 씁니다. 끄면 SSD 기록이 줄어 반응이 조금 나아집니다.",
            category: TweakCategory.Performance,
            specs:
            [
                // 1 = 사용자 관리 · 마지막 액세스 갱신 끔
                RegistryValueSpec.Dword(
                    RegistryRoot.LocalMachine, RegistryPaths.FileSystemKey,
                    "NtfsDisableLastAccessUpdate", applied: 1),

                RegistryValueSpec.Dword(
                    RegistryRoot.LocalMachine, RegistryPaths.FileSystemKey,
                    "NtfsDisable8dot3NameCreation", applied: 1)
            ],
            risk: TweakRisk.Moderate),

        _factory.FromRegistry(
            id: "perf.disable-prefetch",
            title: "Prefetch · Superfetch(SysMain) 끄기",
            description: "Prefetch가 켜져 있으면 백그라운드 디스크가 자주 돕니다. SSD·메모리 충분하면 끄면 더 조용하고 가볍습니다.",
            category: TweakCategory.Performance,
            specs:
            [
                // 0 = 사용 안 함, 3 = 부팅+앱(기본에 가까운 값)
                RegistryValueSpec.Dword(
                    RegistryRoot.LocalMachine, RegistryPaths.PrefetchParameters,
                    "EnablePrefetcher", applied: 0, defaultValue: 3),

                RegistryValueSpec.Dword(
                    RegistryRoot.LocalMachine, RegistryPaths.PrefetchParameters,
                    "EnableSuperfetch", applied: 0, defaultValue: 3)
            ],
            risk: TweakRisk.Moderate),

        _factory.FromRegistry(
            id: "perf.disable-network-throttling",
            title: "멀티미디어 네트워크 스로틀링 끄기",
            description: "네트워크 스로틀링이 켜져 있으면 전송 속도가 일부러 막힐 수 있습니다. 끄면 대역폭을 더 씁니다.",
            category: TweakCategory.Performance,
            specs:
            [
                // 0xFFFFFFFF = 스로틀링 없음
                RegistryValueSpec.Dword(
                    RegistryRoot.LocalMachine, RegistryPaths.MultimediaSystemProfile,
                    "NetworkThrottlingIndex", applied: unchecked((int)0xFFFFFFFF)),

                // 예약 CPU % 를 줄여 포그라운드에 더 양보 (기본 20)
                RegistryValueSpec.Dword(
                    RegistryRoot.LocalMachine, RegistryPaths.MultimediaSystemProfile,
                    "SystemResponsiveness", applied: 10, defaultValue: 20)
            ],
            createKeysOnApply: [(RegistryRoot.LocalMachine, RegistryPaths.MultimediaSystemProfile)],
            risk: TweakRisk.Moderate),

        _factory.FromRegistry(
            id: "perf.disable-error-reporting",
            title: "Windows 오류 보고 끄기",
            description: "오류 보고가 켜져 있으면 멈춘 앱 정보를 백그라운드로 보냅니다. 끄면 전송·관련 프로세스가 줄어듭니다.",
            category: TweakCategory.Performance,
            specs:
            [
                RegistryValueSpec.Dword(
                    RegistryRoot.LocalMachine, RegistryPaths.WindowsErrorReporting,
                    "Disabled", applied: 1)
            ],
            createKeysOnApply: [(RegistryRoot.LocalMachine, RegistryPaths.WindowsErrorReporting)]),

        _factory.FromRegistry(
            id: "perf.disable-remote-assistance",
            title: "원격 지원 끄기",
            description: "원격 지원이 켜져 있으면 불필요하게 열려 있을 수 있습니다. 끄면 쓰지 않는 원격 창구가 닫힙니다.",
            category: TweakCategory.Performance,
            specs:
            [
                RegistryValueSpec.Dword(
                    RegistryRoot.LocalMachine, RegistryPaths.RemoteAssistance,
                    "fAllowToGetHelp", applied: 0, defaultValue: 1)
            ])
    ];
}
