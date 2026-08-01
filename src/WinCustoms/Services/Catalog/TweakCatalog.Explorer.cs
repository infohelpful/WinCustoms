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
            description: "파일 탐색기 왼쪽 사이드바에서 Windows 11 의 '홈' 노드를 감춥니다. '즐겨찾기'와 '최근 항목' 목록도 함께 사라집니다.",
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
            description: "사진 라이브러리를 자동으로 훑어 표시하는 '갤러리' 노드를 감춥니다. 사진이 많은 PC 에서는 탐색기 체감 속도가 좋아집니다.",
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
            id: "explorer.ribbon-ui",
            title: "Windows 10 스타일 리본 탐색기 사용",
            description: "탭 대신 리본 메뉴가 있는 Windows 10 탐색기로 되돌립니다. "
                       + "Windows 11 21H2 · 22H2 에서만 동작하며, 23H2 이후 빌드에서는 효과가 없습니다.",
            category: TweakCategory.Explorer,
            specs:
            [
                // 값 이름이 CLSID, 값은 빈 문자열인 '차단 목록' 형식이다.
                new RegistryValueSpec(
                    RegistryRoot.LocalMachine,
                    RegistryPaths.ShellExtensionsBlocked,
                    RegistryPaths.RibbonExplorerClsid,
                    RegistryValueKind.String,
                    AppliedValue: string.Empty)
            ],
            createKeysOnApply: [(RegistryRoot.LocalMachine, RegistryPaths.ShellExtensionsBlocked)],
            requiresExplorerRestart: true,
            risk: TweakRisk.Moderate),

        _factory.FromRegistry(
            id: "explorer.launch-to-thispc",
            title: "탐색기를 '내 PC'로 열기",
            description: "Win + E 로 탐색기를 열 때 '홈' 대신 '내 PC'가 먼저 표시됩니다.",
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
            description: ".exe · .zip 같은 확장명과 숨김 폴더/파일을 항상 표시합니다. "
                       + "확장자를 위장한 파일을 구분할 수 있어 보안에도 도움이 됩니다.",
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
            description: "항목 사이 여백을 줄여 한 화면에 더 많은 파일을 표시합니다. 터치보다 마우스 위주로 쓸 때 유용합니다.",
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
        description: "'추가 옵션 표시'를 거치지 않고 전체 우클릭 메뉴를 바로 표시합니다. "
                   + "HKCU 범위만 사용하므로 관리자 권한 없이 적용되고, 해제하면 완전히 원래대로 돌아갑니다.",
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
