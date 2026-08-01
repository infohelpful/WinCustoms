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
            description: "Windows 에 숨겨져 있는 Ultimate Performance 전원 구성표를 만들어 활성화합니다. "
                       + "지연 시간이 줄어드는 대신 전력 소비가 늘어나므로 노트북에서는 권장하지 않습니다.",
            category: TweakCategory.Performance,
            apply: ct => _maintenance.EnableUltimatePerformanceAsync(ct),
            restore: ct => _maintenance.DisableUltimatePerformanceAsync(ct),
            detect: () => _maintenance.IsUltimatePerformanceActive(),
            requiresAdmin: true,
            risk: TweakRisk.Moderate),

        _factory.FromRegistry(
            id: "perf.disable-animations",
            title: "창 애니메이션 · 시각 효과 끄기",
            description: "창 최소화/최대화 애니메이션, 투명 효과, 그림자를 꺼서 클릭 반응 속도를 높입니다. "
                       + "Mica 같은 반투명 효과도 함께 사라지므로 외형이 단순해집니다.",
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
            description: "Windows Update 가 그래픽 카드 등 장치 드라이버를 임의로 덮어쓰지 않도록 막습니다. "
                       + "보안 패치와 기능 업데이트는 그대로 설치됩니다.",
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
            description: "응답 없는 앱을 강제 종료하기까지의 대기 시간을 줄여 재부팅과 종료를 빠르게 만듭니다. "
                       + "저장하지 않은 작업이 있으면 손실될 수 있으니 유의하세요.",
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
            risk: TweakRisk.Moderate)
    ];
}
