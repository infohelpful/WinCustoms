using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCustoms.Models;
using WinCustoms.Services;
using WinCustoms.Services.Catalog;

namespace WinCustoms.ViewModels;

/// <summary>
/// 셸 수준의 상태(네비게이션, 관리자 권한 표시, 전역 작업)를 담당한다.
/// 개별 트윅 조작은 각 카테고리 뷰모델의 몫이다.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly ITweakCatalog _catalog;
    private readonly ITweakEngine _engine;
    private readonly IMaintenanceService _maintenance;
    private readonly IElevationService _elevation;
    private readonly IDialogService _dialog;
    private readonly TweakPageViewModelLocator _locator;

    public MainViewModel(
        ITweakCatalog catalog,
        ITweakEngine engine,
        IMaintenanceService maintenance,
        IElevationService elevation,
        IDialogService dialog,
        TweakPageViewModelLocator locator)
    {
        _catalog = catalog;
        _engine = engine;
        _maintenance = maintenance;
        _elevation = elevation;
        _dialog = dialog;
        _locator = locator;

        SelectedTag = NavigationTags.Explorer;
    }

    public bool IsElevated => _elevation.IsElevated;

    public string ElevationText => _elevation.IsElevated
        ? "관리자 권한으로 실행 중"
        : "표준 권한 · 필요할 때만 승격";

    public string AppVersion => "1.0.0";

    [ObservableProperty]
    public partial string SelectedTag { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? GlobalStatus { get; set; }

    /// <summary>전체 카테고리에서 현재 적용된 트윅 수. 시작 화면 요약에 쓴다.</summary>
    [ObservableProperty]
    public partial int TotalAppliedCount { get; set; }

    [RelayCommand]
    public void RefreshAll()
    {
        _engine.RefreshStates(_catalog.All);
        TotalAppliedCount = _catalog.All.Count(t => t.IsToggle && t.IsApplied);

        foreach (var page in _locator.All)
            page.EnsureLoaded();
    }

    /// <summary>트윅을 적용하기 전에 눌러 두면 좋은 안전장치. 상단 바에 항상 노출한다.</summary>
    [RelayCommand]
    private async Task CreateRestorePointAsync(CancellationToken ct)
    {
        if (IsBusy) return;

        var confirmed = await _dialog.ConfirmAsync(
            "시스템 복원 지점 만들기",
            "현재 시스템 상태를 복원 지점으로 저장합니다. 관리자 권한 승인이 필요하며 1~2분 정도 걸릴 수 있습니다.",
            "만들기");

        if (!confirmed) return;

        IsBusy = true;
        GlobalStatus = "복원 지점을 만드는 중...";

        try
        {
            await _maintenance.CreateRestorePointAsync($"WinCustoms {DateTime.Now:yyyy-MM-dd HH:mm}", ct);
            GlobalStatus = "복원 지점을 만들었습니다.";
        }
        catch (ElevationDeniedException)
        {
            GlobalStatus = "관리자 권한 승인이 취소되었습니다.";
        }
        catch (Exception ex)
        {
            GlobalStatus = null;
            await _dialog.ShowMessageAsync("복원 지점 생성 실패", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>모든 카테고리의 트윅을 한 번에 기본값으로 되돌린다.</summary>
    [RelayCommand]
    private async Task RestoreEverythingAsync(CancellationToken ct)
    {
        if (IsBusy) return;

        var applied = _catalog.All.Where(t => t.IsToggle && t.IsApplied).ToList();
        if (applied.Count == 0)
        {
            GlobalStatus = "적용된 트윅이 없습니다.";
            return;
        }

        var confirmed = await _dialog.ConfirmAsync(
            "모든 트윅 되돌리기",
            $"현재 적용된 {applied.Count}개 항목을 전부 Windows 기본 상태로 복원합니다. 계속할까요?",
            "전체 복원");

        if (!confirmed) return;

        IsBusy = true;
        try
        {
            var result = await _engine.RestoreAllAsync(applied, ct);
            GlobalStatus = $"{result.Succeeded}개 항목을 복원했습니다.";
            RefreshAll();
        }
        catch (Exception ex)
        {
            await _dialog.ShowMessageAsync("복원 실패", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>NavigationView 항목의 Tag 값. 문자열 오타를 막기 위해 상수로 관리한다.</summary>
public static class NavigationTags
{
    public const string Explorer = "explorer";
    public const string ContextMenu = "contextmenu";
    public const string Taskbar = "taskbar";
    public const string Privacy = "privacy";
    public const string Debloat = "debloat";
    public const string Winget = "winget";
    public const string SystemBackup = "sysbackup";
    public const string CustomIso = "customiso";
    public const string Performance = "performance";
    public const string PowerTools = "powertools";
    public const string Settings = "settings";

    public static TweakCategory? ToCategory(string? tag) => tag switch
    {
        Explorer => TweakCategory.Explorer,
        Taskbar => TweakCategory.Taskbar,
        Privacy => TweakCategory.Privacy,
        Performance => TweakCategory.Performance,
        PowerTools => TweakCategory.PowerTools,
        _ => null
    };
}
