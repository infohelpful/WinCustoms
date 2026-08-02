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
            description: "가운데 정렬이면 아이콘 위치가 자주 바뀌어 손이 헷갈립니다. 켜면 왼쪽 고정이라 클릭이 익숙해집니다.",
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
            id: "taskbar.never-combine",
            title: "작업 표시줄 단추 결합 안 함",
            description: "창이 하나로 묶이면 원하는 창을 고르기 어렵습니다. 켜면 창마다 단추·이름이 따로 보여 전환이 쉽습니다.",
            category: TweakCategory.Taskbar,
            specs:
            [
                // 0 = 항상 결합, 1 = 가득 찰 때, 2 = 결합 안 함
                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ExplorerAdvanced,
                    "TaskbarGlomLevel", applied: 2, defaultValue: 0),

                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ExplorerAdvanced,
                    "MMTaskbarGlomLevel", applied: 2, defaultValue: 0)
            ],
            requiresExplorerRestart: true),

        _factory.FromRegistry(
            id: "taskbar.hide-system-icons",
            title: "위젯 · 검색 · 작업 보기 · 채팅 · Copilot 아이콘 숨기기",
            description: "검색·작업보기·채팅·Copilot 아이콘을 숨깁니다. "
                         + "위젯(TaskbarDa)은 일부 PC에서 정책으로 잠겨 있어, "
                         + "이미 숨겨져 있거나 잠긴 경우 건너뜁니다.",
            category: TweakCategory.Taskbar,
            specs:
            [
                // TaskbarDa(위젯)는 일부 환경에서 생성/변경이 정책으로 차단된다.
                // 잠긴 PC에서 전체 트윅이 실패하지 않도록 스펙에서 제외한다.

                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ExplorerAdvanced,
                    "ShowTaskViewButton", applied: 0, defaultValue: 1),

                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ExplorerAdvanced,
                    "TaskbarMn", applied: 0, defaultValue: 1),

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
            id: "taskbar.end-task",
            title: "작업 표시줄에서 '작업 종료' 메뉴 표시",
            description: "응답 없는 앱을 작업 관리자까지 가서 끄기 번거롭습니다. 켜면 아이콘 우클릭에 작업 종료가 바로 뜹니다.",
            category: TweakCategory.Taskbar,
            specs:
            [
                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ExplorerAdvanced,
                    "TaskbarEndTask", applied: 1, defaultValue: 0)
            ],
            requiresExplorerRestart: true),

        _factory.FromRegistry(
            id: "taskbar.hide-badges",
            title: "작업 표시줄 알림 배지 숨기기",
            description: "빨간 숫자 배지가 켜져 있으면 시선이 계속 끌립니다. 끄면 아이콘만 남아 덜 산만합니다.",
            category: TweakCategory.Taskbar,
            specs:
            [
                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ExplorerAdvanced,
                    "TaskbarBadges", applied: 0, defaultValue: 1)
            ],
            requiresExplorerRestart: true),

        _factory.FromRegistry(
            id: "taskbar.clock-seconds",
            title: "작업 표시줄 시계에 초 표시",
            description: "초가 없으면 정확한 시각을 보기 어렵습니다. 켜면 시계에 초가 보여 타이밍 확인이 쉽습니다.",
            category: TweakCategory.Taskbar,
            specs:
            [
                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ExplorerAdvanced,
                    "ShowSecondsInSystemClock", applied: 1, defaultValue: 0)
            ],
            requiresExplorerRestart: true),

        _factory.FromRegistry(
            id: "start.disable-web-search",
            title: "시작 검색 인터넷 결과·우측 웹 패널 끄기",
            description: "인터넷 검색이 켜져 있으면 오른쪽에 Bing 결과가 떠서 느리고 산만합니다. 끄면 PC 안 앱·파일·설정만 빠르게 찾습니다.",
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
                    "CortanaConsent", applied: 0),

                RegistryValueSpec.Dword(
                    RegistryRoot.LocalMachine, RegistryPaths.WindowsSearchPolicy,
                    "ConnectedSearchUseWeb", applied: 0),

                RegistryValueSpec.Dword(
                    RegistryRoot.LocalMachine, RegistryPaths.WindowsSearchPolicy,
                    "ConnectedSearchUseWebOverMeteredConnections", applied: 0),

                RegistryValueSpec.Dword(
                    RegistryRoot.LocalMachine, RegistryPaths.WindowsSearchPolicy,
                    "DisableWebSearch", applied: 1),

                RegistryValueSpec.Dword(
                    RegistryRoot.LocalMachine, RegistryPaths.WindowsSearchPolicy,
                    "EnableDynamicContentInWSB", applied: 0),

                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.SearchSettings,
                    "IsDynamicSearchBoxEnabled", applied: 0, defaultValue: 1)
            ],
            createKeysOnApply:
            [
                (RegistryRoot.CurrentUser, RegistryPaths.ExplorerPolicyKeyUser),
                (RegistryRoot.LocalMachine, RegistryPaths.WindowsSearchPolicy),
                (RegistryRoot.CurrentUser, RegistryPaths.SearchSettings)
            ],
            requiresExplorerRestart: true),

        SearchBrowserRedirectTweak(),

        _factory.FromRegistry(
            id: "start.hide-recommended",
            title: "시작 메뉴 '추천' 영역 비우기",
            description: "추천 영역이 켜져 있으면 최근 파일·추천 앱이 노출됩니다. 끄면 목록이 비워져 개인 기록이 덜 보입니다.",
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

                RegistryValueSpec.Dword(
                    RegistryRoot.LocalMachine, RegistryPaths.StartPolicyDevice,
                    "HideRecommendedSection", applied: 1)
            ],
            createKeysOnApply: [(RegistryRoot.LocalMachine, RegistryPaths.StartPolicyDevice)],
            requiresExplorerRestart: true,
            risk: TweakRisk.Moderate),

        _factory.FromRegistry(
            id: "start.hide-recent-jumplists",
            title: "점프 목록·최근 연 항목 끄기",
            description: "최근 파일이 켜져 있으면 작업표시줄 우클릭·시작 메뉴에 기록이 쌓입니다. 끄면 최근 목록이 비워집니다.",
            category: TweakCategory.Taskbar,
            specs:
            [
                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ExplorerAdvanced,
                    "Start_TrackDocs", applied: 0, defaultValue: 1),

                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ExplorerAdvanced,
                    "Start_TrackProgs", applied: 0, defaultValue: 1),

                // 0 = 점프 목록에 최근/자주 사용 안 함
                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ExplorerAdvanced,
                    "Start_TrackRarelyOpenedDocs", applied: 0)
            ],
            requiresExplorerRestart: true),

        _factory.FromRegistry(
            id: "start.no-recommended-section-policy",
            title: "시작 메뉴 계정 알림·추천 팁 끄기",
            description: "계정·Microsoft 서비스 알림이 켜져 있으면 시작 메뉴에 안내가 끼어듭니다. 끄면 그런 팁이 덜 뜹니다.",
            category: TweakCategory.Taskbar,
            specs:
            [
                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ExplorerAdvanced,
                    "Start_AccountNotifications", applied: 0, defaultValue: 1),

                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ContentDeliveryManager,
                    "SubscribedContent-338388Enabled", applied: 0, defaultValue: 1)
            ],
            requiresExplorerRestart: true),

        ClassicStartMenuTweak()
    ];

    /// <summary>
    /// 시작/시스템 링크의 microsoft-edge: 강제 호출을 선택한 브라우저로 넘긴다.
    /// 적용 시 설치된 브라우저 목록에서 고른다.
    /// </summary>
    private TweakItem SearchBrowserRedirectTweak() => _factory.Custom(
        id: "start.search-browser",
        title: "시작 검색·시스템 링크를 선택한 브라우저로 열기",
        description: "웹 결과가 Edge로만 열리면 쓰는 브라우저와 어긋납니다. 켜면 설치한 브라우저 중 하나를 골라 그쪽으로 엽니다. "
                   + "인터넷 결과 자체를 없애려면 위 '웹 패널 끄기'를 함께 켜세요.",
        category: TweakCategory.Taskbar,
        apply: async ct =>
        {
            var browsers = _browsers.ListInstalled()
                .Where(b => !b.IsEdge)
                .ToList();

            if (browsers.Count == 0)
                throw new InvalidOperationException("Edge 외에 선택 가능한 브라우저가 없습니다. Chrome·Firefox 등을 설치한 뒤 다시 시도하세요.");

            var current = _browsers.CurrentTargetPath();
            var options = browsers
                .Select(b => (
                    Label: string.Equals(b.ExecutablePath, current, StringComparison.OrdinalIgnoreCase)
                        ? $"{b.Name} (현재)"
                        : b.Name,
                    Value: b))
                .ToList();

            var picked = await _dialog.PickOptionAsync(
                "브라우저 선택",
                "시작 검색·시스템 링크가 Edge 대신 열릴 브라우저를 고르세요.",
                options,
                "이 브라우저 사용").ConfigureAwait(true);

            if (picked is null)
                throw new InvalidOperationException("브라우저 선택이 취소되었습니다.");

            await _browsers.ApplyRedirectAsync(picked, ct).ConfigureAwait(true);
        },
        restore: ct => _browsers.ClearRedirectAsync(ct),
        detect: () => _browsers.IsRedirectActive(),
        requiresExplorerRestart: true,
        risk: TweakRisk.Moderate);

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
            Description = "기본 시작 메뉴는 맞춤형·광고성 영역이 많습니다. Open-Shell을 쓰면 예전처럼 단순하고 빠른 메뉴를 쓸 수 있습니다.",
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
