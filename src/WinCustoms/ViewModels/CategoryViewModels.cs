using WinCustoms.Models;
using WinCustoms.Services;
using WinCustoms.Services.Catalog;

namespace WinCustoms.ViewModels;

public sealed class ExplorerViewModel(
    ITweakCatalog catalog, ITweakEngine engine, IShellService shell, IDialogService dialog)
    : TweakPageViewModelBase(
        TweakCategory.Explorer,
        "탐색기 및 우클릭 메뉴",
        "파일 탐색기의 동작과 마우스 오른쪽 버튼 메뉴를 Windows 10 에 가깝게 조정합니다.",
        catalog, engine, shell, dialog);

public sealed class TaskbarViewModel(
    ITweakCatalog catalog, ITweakEngine engine, IShellService shell, IDialogService dialog)
    : TweakPageViewModelBase(
        TweakCategory.Taskbar,
        "작업 표시줄 및 시작 메뉴",
        "아이콘 정렬, 불필요한 버튼, 검색 동작을 정리해 작업 표시줄을 단순하게 만듭니다.",
        catalog, engine, shell, dialog);

public sealed class PrivacyViewModel(
    ITweakCatalog catalog, ITweakEngine engine, IShellService shell, IDialogService dialog)
    : TweakPageViewModelBase(
        TweakCategory.Privacy,
        "개인정보 및 광고 제거",
        "진단 데이터 전송, 맞춤 광고, 백그라운드 AI 기능을 끄고 기본 앱을 정리합니다.",
        catalog, engine, shell, dialog);

public sealed class PerformanceViewModel(
    ITweakCatalog catalog, ITweakEngine engine, IShellService shell, IDialogService dialog)
    : TweakPageViewModelBase(
        TweakCategory.Performance,
        "성능 최적화",
        "전원 구성표와 시각 효과를 조정해 응답 속도를 우선하도록 바꿉니다.",
        catalog, engine, shell, dialog);

public sealed class PowerToolsViewModel(
    ITweakCatalog catalog, ITweakEngine engine, IShellService shell, IDialogService dialog)
    : TweakPageViewModelBase(
        TweakCategory.PowerTools,
        "파워유저 도구",
        "소유권 가져오기, 터미널 열기 같은 관리자용 우클릭 메뉴와 유지 보수 도구입니다.",
        catalog, engine, shell, dialog);

/// <summary>
/// 하나의 <c>TweakListPage</c> 가 모든 카테고리를 처리하기 위해 필요한 뷰모델 해석기.
/// 네비게이션 파라미터로 받은 카테고리에 맞는 뷰모델을 돌려준다.
/// </summary>
public sealed class TweakPageViewModelLocator(
    ExplorerViewModel explorer,
    TaskbarViewModel taskbar,
    PrivacyViewModel privacy,
    PerformanceViewModel performance,
    PowerToolsViewModel powerTools)
{
    public TweakPageViewModelBase Resolve(TweakCategory category) => category switch
    {
        TweakCategory.Explorer => explorer,
        TweakCategory.Taskbar => taskbar,
        TweakCategory.Privacy => privacy,
        TweakCategory.Performance => performance,
        TweakCategory.PowerTools => powerTools,
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };

    public IReadOnlyList<TweakPageViewModelBase> All => [explorer, taskbar, privacy, performance, powerTools];
}
