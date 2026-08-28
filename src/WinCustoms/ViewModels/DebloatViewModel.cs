using System.Collections.ObjectModel;
using System.ComponentModel;
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
    private readonly List<AppxPackageInfo> _allPackages = [];

    /// <summary>생성자에서 기본값을 넣는 동안에는 다시 읽어오지 않도록 하는 플래그.</summary>
    private readonly bool _initialized;

    public DebloatViewModel(IAppxService appx, IDialogService dialog)
    {
        _appx = appx;
        _dialog = dialog;
        ShowInstalledOnly = true;
        _initialized = true;
    }

    public ObservableCollection<AppxPackageInfo> Packages { get; } = [];

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    /// <summary>켜면 설치된 앱만 목록에 표시한다.</summary>
    [ObservableProperty]
    public partial bool ShowInstalledOnly { get; set; }

    /// <summary>목록이 비었을 때 안내 문구를 띄우기 위한 플래그.</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    public string EmptyMessage => ShowInstalledOnly
        ? "정리 대상으로 등록된 기본 앱이 이 PC 에는 설치되어 있지 않습니다. 오른쪽 '설치된 앱만 보기'를 끄면 전체 목록을 볼 수 있습니다."
        : "정리 대상 목록이 비어 있습니다.";

    public string WarningNotice => "삭제된 앱, 특히 마이크로소프트 스토어 앱을 다시 설치하는 것은 어려울 수 있다는 점에 유의하십시오 .";

    public string Title => "기본 앱 정리";

    public string Subtitle => "미리 설치된 앱을 선택해 제거합니다. 삭제된 앱, 특히 마이크로소프트 스토어 앱을 다시 설치하는 것은 어려울 수 있다는 점에 유의하십시오 .";

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct)
    {
        if (IsBusy) return;

        IsBusy = true;
        StatusMessage = "설치된 앱을 확인하는 중...";

        try
        {
            DetachPackageHandlers();
            _allPackages.Clear();

            var packages = await _appx.LoadCatalogAsync(ct);
            _allPackages.AddRange(packages);
            AttachPackageHandlers();

            ApplyFilter();

            var installedCount = _allPackages.Count(p => p.IsInstalled);
            StatusMessage = $"정리 대상 {_allPackages.Count}개 중 {installedCount}개가 이 PC 에 설치되어 있습니다.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"앱 목록을 읽지 못했습니다: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnShowInstalledOnlyChanged(bool value)
    {
        if (_initialized) ApplyFilter();
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
        foreach (var package in _allPackages)
            package.IsSelected = false;

        StatusMessage = null;
    }

    [RelayCommand]
    private async Task RemoveSelectedAsync(CancellationToken ct)
    {
        if (IsBusy) return;

        var targets = _allPackages.Where(p => p.IsSelected && p.IsInstalled).ToList();
        if (targets.Count == 0)
        {
            StatusMessage = "제거할 앱을 선택하세요.";
            return;
        }

        var names = string.Join("\n· ", targets.Select(t => t.DisplayName));
        var confirmed = await _dialog.ConfirmAsync(
            $"{targets.Count}개 앱을 제거할까요?",
            $"· {names}\n\n{WarningNotice}\n\n모든 사용자 계정과 프로비저닝(재설치) 목록에서 제거를 시도합니다.",
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
                }
                catch (Exception ex)
                {
                    package.LastError = ex.Message;
                }
                finally
                {
                    package.IsBusy = false;
                }
            }

            StatusMessage = "설치 상태 새로고침 중...";
            await _appx.RefreshInstalledStateAsync(_allPackages, ct);
            ApplyFilter();

            // 최종 실제 설치 상태 기준으로 성공/실패 정확히 판별
            foreach (var target in targets)
            {
                if (!target.IsInstalled)
                {
                    target.IsSelected = false;
                    target.LastError = null;
                    removed++;
                }
                else
                {
                    failed.Add(target.DisplayName);
                }
            }

            var installedCount = _allPackages.Count(p => p.IsInstalled);
            StatusMessage = failed.Count == 0
                ? $"{removed}개 앱을 제거했습니다. (현재 설치된 기본 앱: {installedCount}개)"
                : $"{removed}개 제거 · {failed.Count}개 실패 (현재 설치된 기본 앱: {installedCount}개)";

            if (failed.Count > 0)
            {
                await _dialog.ShowMessageAsync(
                    "일부 앱을 제거하지 못했습니다",
                    "시스템에서 보호하는 앱이거나 Windows Update 로 다시 깔리는 경우일 수 있습니다.\n\n· "
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

    private void ApplyFilter()
    {
        Packages.Clear();
        foreach (var package in _allPackages.Where(p => !ShowInstalledOnly || p.IsInstalled))
            Packages.Add(package);

        IsEmpty = Packages.Count == 0;
        OnPropertyChanged(nameof(EmptyMessage));
    }

    private void AttachPackageHandlers()
    {
        foreach (var package in _allPackages)
            package.PropertyChanged += OnPackagePropertyChanged;
    }

    private void DetachPackageHandlers()
    {
        foreach (var package in _allPackages)
            package.PropertyChanged -= OnPackagePropertyChanged;
    }

    private void OnPackagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppxPackageInfo.IsInstalled))
            ApplyFilter();
    }
}
