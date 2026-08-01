using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCustoms.Services;

namespace WinCustoms.ViewModels;

/// <summary>
/// 기본 제공 앱(Bloatware) 정리.
/// AppX 제거는 되돌리기가 완전하지 않으므로 선택 → 확인 → 실행의 3단계를 거친다.
/// </summary>
public sealed partial class DebloatViewModel : ObservableObject
{
    private readonly IAppxService _appx;
    private readonly IDialogService _dialog;

    /// <summary>생성자에서 기본값을 넣는 동안에는 다시 읽어오지 않도록 하는 플래그.</summary>
    private readonly bool _initialized;

    public DebloatViewModel(IAppxService appx, IDialogService dialog)
    {
        _appx = appx;
        _dialog = dialog;

        HideUninstalled = true;
        _initialized = true;
    }

    public ObservableCollection<AppxPackageInfo> Packages { get; } = [];

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    [ObservableProperty]
    public partial bool HideUninstalled { get; set; }

    /// <summary>목록이 비었을 때 안내 문구를 띄우기 위한 플래그.</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    public string EmptyMessage => HideUninstalled
        ? "정리 대상으로 등록된 기본 앱이 이 PC 에는 설치되어 있지 않습니다. 아래 '설치되지 않은 항목도 표시'를 켜면 전체 목록을 볼 수 있습니다."
        : "정리 대상 목록이 비어 있습니다.";

    public string Title => "기본 앱 정리";

    public string Subtitle => "미리 설치된 앱을 선택해 제거합니다. 제거한 앱은 Microsoft Store 에서 다시 설치할 수 있지만 "
                            + "자동으로 복구되지는 않으니 필요한 앱은 남겨 두세요.";

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct)
    {
        if (IsBusy) return;

        IsBusy = true;
        StatusMessage = "설치된 앱을 확인하는 중...";

        try
        {
            var packages = await _appx.LoadCatalogAsync(ct);

            Packages.Clear();
            foreach (var package in packages.Where(p => !HideUninstalled || p.IsInstalled))
                Packages.Add(package);

            var installedCount = packages.Count(p => p.IsInstalled);
            StatusMessage = $"정리 대상 {packages.Count}개 중 {installedCount}개가 이 PC 에 설치되어 있습니다.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"앱 목록을 읽지 못했습니다: {ex.Message}";
        }
        finally
        {
            IsEmpty = Packages.Count == 0;
            OnPropertyChanged(nameof(EmptyMessage));
            IsBusy = false;
        }
    }

    partial void OnHideUninstalledChanged(bool value)
    {
        if (_initialized) _ = LoadAsync(CancellationToken.None);
    }

    [RelayCommand]
    private void SelectRecommended()
    {
        foreach (var package in Packages)
            package.IsSelected = package.IsInstalled && package.RecommendedForRemoval;

        StatusMessage = $"권장 항목 {Packages.Count(p => p.IsSelected)}개를 선택했습니다.";
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var package in Packages)
            package.IsSelected = false;

        StatusMessage = null;
    }

    [RelayCommand]
    private async Task RemoveSelectedAsync(CancellationToken ct)
    {
        if (IsBusy) return;

        var targets = Packages.Where(p => p.IsSelected && p.IsInstalled).ToList();
        if (targets.Count == 0)
        {
            StatusMessage = "제거할 앱을 선택하세요.";
            return;
        }

        var names = string.Join("\n· ", targets.Select(t => t.DisplayName));
        var confirmed = await _dialog.ConfirmAsync(
            $"{targets.Count}개 앱을 제거할까요?",
            $"· {names}\n\n현재 사용자 계정에서 제거됩니다. 되돌리려면 Microsoft Store 에서 직접 다시 설치해야 합니다.",
            "제거");

        if (!confirmed) return;

        IsBusy = true;
        var removed = 0;
        var failed = new List<string>();

        try
        {
            foreach (var package in targets)
            {
                ct.ThrowIfCancellationRequested();

                package.IsBusy = true;
                package.LastError = null;
                StatusMessage = $"{package.DisplayName} 제거 중...";

                try
                {
                    await _appx.RemoveAsync(package, ct);
                    package.IsSelected = false;
                    removed++;
                }
                catch (Exception ex)
                {
                    package.LastError = ex.Message;
                    failed.Add(package.DisplayName);
                }
                finally
                {
                    package.IsBusy = false;
                }
            }

            StatusMessage = failed.Count == 0
                ? $"{removed}개 앱을 제거했습니다."
                : $"{removed}개 제거 · {failed.Count}개 실패";

            if (failed.Count > 0)
            {
                await _dialog.ShowMessageAsync(
                    "일부 앱을 제거하지 못했습니다",
                    "시스템에서 보호하는 앱이거나 다른 사용자 계정에 설치된 경우일 수 있습니다.\n\n· "
                    + string.Join("\n· ", failed));
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "작업이 취소되었습니다.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task OpenStoreAsync(AppxPackageInfo? package)
        => package is null ? Task.CompletedTask : _appx.OpenStoreAsync(package);
}
