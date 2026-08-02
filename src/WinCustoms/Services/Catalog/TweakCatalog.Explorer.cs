using Microsoft.Win32;
using WinCustoms.Common;
using WinCustoms.Models;

namespace WinCustoms.Services.Catalog;

public sealed partial class TweakCatalog
{
    private IReadOnlyList<TweakItem> BuildExplorerTweaks() =>
    [
        ClassicContextMenuTweak(),

        _factory.FromRegistry(
            id: "explorer.hide-home",
            title: "탐색기 탐색 창에서 '홈' 숨기기",
            description: "홈이 켜져 있으면 최근 파일이 노출되고 사이드바가 복잡해집니다. 끄면 즐겨찾기·최근 목록이 숨겨져 정돈됩니다.",
            category: TweakCategory.Explorer,
            specs:
            [
                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser,
                    RegistryPaths.HomeNodeKey,
                    RegistryPaths.PinnedToNameSpaceTree,
                    applied: 0)
            ],
            requiresExplorerRestart: true),

        _factory.FromRegistry(
            id: "explorer.hide-gallery",
            title: "탐색기 탐색 창에서 '갤러리' 숨기기",
            description: "갤러리가 켜져 있으면 사진을 백그라운드로 훑어 탐색기가 느려질 수 있습니다. 끄면 사이드바가 가볍고 빨라집니다.",
            category: TweakCategory.Explorer,
            specs:
            [
                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser,
                    RegistryPaths.GalleryNodeKey,
                    RegistryPaths.PinnedToNameSpaceTree,
                    applied: 0)
            ],
            requiresExplorerRestart: true),

        _factory.FromRegistry(
            id: "explorer.launch-to-thispc",
            title: "탐색기를 '내 PC'로 열기",
            description: "홈으로 열리면 최근·추천 화면부터 뜹니다. 켜면 드라이브 목록(내 PC)이 바로 보여 이동이 빠릅니다.",
            category: TweakCategory.Explorer,
            specs:
            [
                // 1 = 내 PC, 2 = 홈(기본값), 3 = 다운로드
                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser,
                    RegistryPaths.ExplorerAdvanced,
                    "LaunchTo",
                    applied: 1,
                    defaultValue: 2)
            ],
            requiresExplorerRestart: true),

        _factory.FromRegistry(
            id: "explorer.show-extensions-hidden",
            title: "파일 확장명 및 숨김 파일 표시",
            description: "확장명·숨김 파일이 꺼져 있으면 위험한 파일을 구분하기 어렵습니다. 켜면 .exe 위장과 숨김 항목을 바로 볼 수 있습니다.",
            category: TweakCategory.Explorer,
            specs:
            [
                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ExplorerAdvanced,
                    "HideFileExt", applied: 0, defaultValue: 1),

                // 1 = 숨김 파일 표시, 2 = 숨김(기본값)
                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ExplorerAdvanced,
                    "Hidden", applied: 1, defaultValue: 2)
            ],
            requiresExplorerRestart: true),

        _factory.FromRegistry(
            id: "explorer.compact-mode",
            title: "간격 좁은(Compact) 보기 사용",
            description: "기본 간격은 한 화면에 파일이 적게 보입니다. 켜면 목록이 촘촘해져 스크롤이 줄어듭니다.",
            category: TweakCategory.Explorer,
            specs:
            [
                RegistryValueSpec.Dword(
                    RegistryRoot.CurrentUser, RegistryPaths.ExplorerAdvanced,
                    "UseCompactMode", applied: 1, defaultValue: 0)
            ],
            requiresExplorerRestart: true)
    ];

    /// <summary>
    /// Windows 10 스타일 클래식 우클릭 메뉴.
    ///
    /// 원리: Windows 11 의 새 컨텍스트 메뉴는 CLSID {86ca1aa0-…} 셸 확장이 담당한다.
    /// HKCU 아래에 같은 CLSID 의 InprocServer32 키를 만들고 기본값을 "빈 문자열"로 두면
    /// 셸이 확장을 로드하지 못해 클래식 메뉴로 되돌아간다.
    /// 복원은 이 CLSID 키를 통째로 지우면 되므로 시스템 파일을 건드리지 않는다.
    /// </summary>
    private TweakItem ClassicContextMenuTweak() => _factory.FromRegistry(
        id: "explorer.classic-context-menu",
        title: "Windows 10 스타일 클래식 우클릭 메뉴",
        description: "새 우클릭 메뉴는 자주 쓰는 항목이 추가 옵션 안에 숨겨져 클릭이 늘어납니다. 켜면 예전처럼 전체 메뉴가 바로 뜹니다.",
        category: TweakCategory.Explorer,
        specs:
        [
            // 기본값(Default)을 빈 문자열로 설정하는 것이 핵심이다. 키만 만들면 동작하지 않는다.
            new RegistryValueSpec(
                RegistryRoot.CurrentUser,
                RegistryPaths.ClassicContextMenuInprocKey,
                Name: string.Empty,
                Kind: RegistryValueKind.String,
                AppliedValue: string.Empty)
        ],
        createKeysOnApply: [(RegistryRoot.CurrentUser, RegistryPaths.ClassicContextMenuInprocKey)],
        deleteKeysOnRestore: [(RegistryRoot.CurrentUser, RegistryPaths.ClassicContextMenuKey)],
        requiresExplorerRestart: true,
        detect: () =>
            _registry.KeyExists(RegistryRoot.CurrentUser, RegistryPaths.ClassicContextMenuInprocKey)
            && _registry.ReadValue(RegistryRoot.CurrentUser, RegistryPaths.ClassicContextMenuInprocKey, string.Empty) is string s
            && s.Length == 0);
}
