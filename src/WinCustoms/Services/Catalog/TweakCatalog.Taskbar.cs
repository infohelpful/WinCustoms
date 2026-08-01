using WinCustoms.Common;
using WinCustoms.Models;

namespace WinCustoms.Services.Catalog;

public sealed partial class TweakCatalog
{
    private IReadOnlyList<TweakItem> BuildTaskbarTweaks() =>
    [
        _factory.FromRegistry(
            id: "taskbar.align-left",
            title: "작업 표시줄 왼쪽 정렬",
            description: "시작 버튼과 아이콘을 Windows 10 처럼 화면 왼쪽으로 정렬합니다.",
            category: TweakCategory.Taskbar,
            specs:
            [
                // 0 = 왼쪽, 1 = 가운데(기본값)
                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ExplorerAdvanced,
                    "TaskbarAl", applied: 0, defaultValue: 1)
            ],
            requiresExplorerRestart: true),

        _factory.FromRegistry(
            id: "taskbar.hide-system-icons",
            title: "위젯 · 검색 · 작업 보기 · Copilot 아이콘 숨기기",
            description: "작업 표시줄에서 잘 쓰지 않는 기본 아이콘을 한 번에 정리합니다. 기능 자체가 삭제되는 것은 아니며 언제든 되돌릴 수 있습니다.",
            category: TweakCategory.Taskbar,
            specs:
            [
                // 위젯(날씨)
                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ExplorerAdvanced,
                    "TaskbarDa", applied: 0, defaultValue: 1),

                // 작업 보기
                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ExplorerAdvanced,
                    "ShowTaskViewButton", applied: 0, defaultValue: 1),

                // Copilot 버튼
                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ExplorerAdvanced,
                    "ShowCopilotButton", applied: 0, defaultValue: 1),

                // 검색 상자 (0 = 숨김, 1 = 아이콘, 2 = 상자, 3 = 아이콘 + 레이블)
                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.SearchKey,
                    "SearchboxTaskbarMode", applied: 0, defaultValue: 3)
            ],
            requiresExplorerRestart: true),

        _factory.FromRegistry(
            id: "taskbar.clock-seconds",
            title: "작업 표시줄 시계에 초 표시",
            description: "시계를 HH:mm:ss 형식으로 표시합니다. 시스템 리소스를 아주 조금 더 사용합니다.",
            category: TweakCategory.Taskbar,
            specs:
            [
                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ExplorerAdvanced,
                    "ShowSecondsInSystemClock", applied: 1, defaultValue: 0)
            ],
            requiresExplorerRestart: true),

        _factory.FromRegistry(
            id: "start.disable-bing-search",
            title: "시작 메뉴 Bing 웹 검색 끄기",
            description: "시작 메뉴에서 검색할 때 인터넷 결과를 제외하고 로컬 PC 의 앱·파일·설정만 찾습니다. 검색 반응 속도가 눈에 띄게 빨라집니다.",
            category: TweakCategory.Taskbar,
            specs:
            [
                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ExplorerPolicyKeyUser,
                    "DisableSearchBoxSuggestions", applied: 1),

                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.SearchKey,
                    "BingSearchEnabled", applied: 0, defaultValue: 1),

                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.SearchKey,
                    "CortanaConsent", applied: 0)
            ],
            createKeysOnApply: [(RegistryRoot.CurrentUser, RegistryPaths.ExplorerPolicyKeyUser)],
            requiresExplorerRestart: true),

        _factory.FromRegistry(
            id: "start.hide-recommended",
            title: "시작 메뉴 '추천' 영역 비우기",
            description: "최근 연 파일과 추천 앱이 시작 메뉴에 쌓이지 않도록 합니다. "
                       + "영역 자체를 없애려면 Windows 11 Pro/Enterprise 정책이 필요하며, Home 에서는 목록만 비워집니다.",
            category: TweakCategory.Taskbar,
            specs:
            [
                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ExplorerAdvanced,
                    "Start_TrackDocs", applied: 0, defaultValue: 1),

                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ExplorerAdvanced,
                    "Start_TrackProgs", applied: 0, defaultValue: 1),

                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ExplorerAdvanced,
                    "Start_IrisRecommendations", applied: 0, defaultValue: 1),

                // 정책 기반 완전 제거 (Pro/Enterprise)
                RegistryValueSpec.Dword(
                    RegistryRoot.LocalMachine, RegistryPaths.StartPolicyDevice,
                    "HideRecommendedSection", applied: 1)
            ],
            createKeysOnApply: [(RegistryRoot.LocalMachine, RegistryPaths.StartPolicyDevice)],
            requiresExplorerRestart: true,
            risk: TweakRisk.Moderate),

        ClassicStartMenuTweak()
    ];

    /// <summary>
    /// Windows 7 스타일 시작 메뉴는 서드파티 셸(Open-Shell / StartAllBack)이 필요하다.
    /// 임의로 설치하지 않고, 설치 여부를 감지해 상태를 보여주고 안내만 제공한다.
    /// </summary>
    private TweakItem ClassicStartMenuTweak()
    {
        const string openShellKey = @"SOFTWARE\OpenShell\StartMenu";
        const string startAllBackKey = @"Software\StartIsBack";

        bool IsClassicShellInstalled()
            => _registry.KeyExists(RegistryRoot.LocalMachine, openShellKey)
               || _registry.KeyExists(RegistryRoot.CurrentUser, startAllBackKey);

        return new TweakItem
        {
            Id = "start.classic-start-menu",
            Title = "Windows 7 스타일 시작 메뉴 (Open-Shell)",
            Description = "Windows 자체 설정으로는 구현할 수 없어 Open-Shell 같은 대체 셸이 필요합니다. "
                        + "버튼을 누르면 공식 배포 페이지를 열어 드립니다. 설치되어 있으면 자동으로 감지됩니다.",
            Category = TweakCategory.Taskbar,
            Kind = TweakKind.Action,
            ActionText = "설치 안내 열기",
            Risk = TweakRisk.Safe,
            LearnMoreUrl = "https://github.com/Open-Shell/Open-Shell-Menu/releases",
            DetectApplied = IsClassicShellInstalled,
            ApplyAction = async _ => await _shell.OpenUrlAsync("https://github.com/Open-Shell/Open-Shell-Menu/releases").ConfigureAwait(false),
            RestoreAction = _ => Task.CompletedTask
        };
    }
}
