using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCustoms.Services;

namespace WinCustoms.ViewModels;

/// <summary>
/// winget 으로 프로그램을 설치한다.
/// 추천 목록 탭과 실시간 검색 탭을 제공한다.
/// </summary>
public sealed partial class WingetViewModel : ObservableObject
{
    private readonly IWingetService _winget;
    private readonly IDialogService _dialog;
    private readonly IShellService _shell;

    private readonly List<WingetPackageInfo> _catalog = [];
    private readonly List<WingetPackageInfo> _searchResults = [];
    private readonly bool _initialized;

    public WingetViewModel(IWingetService winget, IDialogService dialog, IShellService shell)
    {
        _winget = winget;
        _dialog = dialog;
        _shell = shell;

        CatalogFilter = string.Empty;
        SearchQuery = string.Empty;
        HideInstalled = true;
        IsCatalogTabSelected = true;
        _initialized = true;
    }

    public ObservableCollection<WingetPackageInfo> Packages { get; } = [];

    public string Title => "프로그램 설치";

    public string Subtitle => IsSearchTabSelected
        ? "winget 저장소에서 프로그램을 검색한 뒤 선택해 설치합니다."
        : "Windows 11 설치 직후에 자주 쓰는 셸·런타임·필수 유틸입니다. "
          + "'추천 항목 선택'으로 ExplorerPatcher·Open-Shell 등을 한 번에 고를 수 있습니다.";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    [ObservableProperty]
    public partial bool WingetAvailable { get; set; }

    [ObservableProperty]
    public partial bool HideInstalled { get; set; }

    [ObservableProperty]
    public partial bool IsCatalogTabSelected { get; set; }

    [ObservableProperty]
    public partial bool IsSearchTabSelected { get; set; }

    /// <summary>추천 목록 안에서의 로컬 필터.</summary>
    [ObservableProperty]
    public partial string CatalogFilter { get; set; }

    /// <summary>winget search 에 넘기는 검색어.</summary>
    [ObservableProperty]
    public partial string SearchQuery { get; set; }

    public bool HasPackages => Packages.Count > 0;

    /// <summary>목록이 비어 있는 새로 고침/검색 대기 중에만 중앙 ProgressRing.</summary>
    public bool ShowCenterBusy => IsBusy && !HasPackages;

    public bool ShowCatalogActions => IsCatalogTabSelected && WingetAvailable;

    public string EmptyMessage
    {
        get
        {
            if (!WingetAvailable)
                return "이 PC 에서 winget 을 찾지 못했습니다. Microsoft Store 의 '앱 설치 관리자'를 설치·업데이트한 뒤 새로 고침하세요.";

            if (IsSearchTabSelected)
            {
                if (string.IsNullOrWhiteSpace(SearchQuery))
                    return "프로그램 이름이나 ID를 입력한 뒤 검색하세요. 예: chrome, 7zip, notepad++";
                return "검색 결과가 없습니다. 다른 키워드로 다시 시도하세요.";
            }

            return HideInstalled
                ? "표시할 항목이 없습니다. 이미 설치된 프로그램을 숨기고 있거나 필터에 맞는 항목이 없습니다."
                : "표시할 항목이 없습니다.";
        }
    }

    public void SelectCatalogTab()
    {
        if (IsCatalogTabSelected) return;
        IsCatalogTabSelected = true;
        IsSearchTabSelected = false;
        ApplyVisibleList();
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(ShowCatalogActions));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    public void SelectSearchTab()
    {
        if (IsSearchTabSelected) return;
        IsSearchTabSelected = true;
        IsCatalogTabSelected = false;
        ApplyVisibleList();
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(ShowCatalogActions));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct)
    {
        if (IsBusy) return;

        IsBusy = true;
        StatusMessage = "winget 과 설치 상태를 확인하는 중...";

        try
        {
            WingetAvailable = await _winget.IsAvailableAsync(ct);
            if (!WingetAvailable)
            {
                _catalog.Clear();
                Packages.Clear();
                NotifyListChanged();
                StatusMessage = "winget 을 찾을 수 없습니다.";
                return;
            }

            var packages = await _winget.LoadCatalogAsync(ct);
            _catalog.Clear();
            _catalog.AddRange(packages);

            if (IsCatalogTabSelected)
                ApplyVisibleList();

            var installed = _catalog.Count(p => p.IsInstalled);
            StatusMessage = $"추천 목록 {_catalog.Count}개 · 이 PC 에 설치됨 {installed}개";
        }
        catch (Exception ex)
        {
            StatusMessage = $"목록을 읽지 못했습니다: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(EmptyMessage));
        }
    }

    [RelayCommand]
    private async Task SearchAsync(CancellationToken ct)
    {
        if (IsBusy) return;

        if (!WingetAvailable)
        {
            StatusMessage = "winget 이 없어 검색할 수 없습니다.";
            return;
        }

        var query = SearchQuery?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            StatusMessage = "검색어를 입력하세요.";
            OnPropertyChanged(nameof(EmptyMessage));
            return;
        }

        if (!IsSearchTabSelected)
            SelectSearchTab();

        IsBusy = true;
        StatusMessage = $"'{query}' 검색 중...";

        try
        {
            var results = await _winget.SearchAsync(query, ct);
            _searchResults.Clear();
            _searchResults.AddRange(results);
            ApplyVisibleList();

            StatusMessage = Packages.Count == 0
                ? $"'{query}' 검색 결과 없음"
                : $"'{query}' 검색 결과 {Packages.Count}개"
                  + (HideInstalled && results.Count > Packages.Count
                      ? $" (설치됨 {results.Count - Packages.Count}개 숨김)"
                      : string.Empty);
        }
        catch (Exception ex)
        {
            _searchResults.Clear();
            ApplyVisibleList();
            StatusMessage = $"검색 실패: {SanitizeWingetError(ex.Message)}";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(EmptyMessage));
        }
    }

    partial void OnHideInstalledChanged(bool value)
    {
        if (!_initialized) return;
        ApplyVisibleList();
    }

    partial void OnCatalogFilterChanged(string value)
    {
        if (_initialized && IsCatalogTabSelected)
            ApplyVisibleList();
    }

    private void ApplyVisibleList()
    {
        Packages.Clear();

        if (IsSearchTabSelected)
        {
            foreach (var package in _searchResults)
            {
                if (HideInstalled && package.IsInstalled) continue;
                Packages.Add(package);
            }

            NotifyListChanged();
            return;
        }

        var term = CatalogFilter?.Trim() ?? string.Empty;
        foreach (var package in _catalog)
        {
            if (HideInstalled && package.IsInstalled) continue;
            if (term.Length > 0
                && !package.DisplayName.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                && !package.Id.Contains(term, StringComparison.OrdinalIgnoreCase)
                && !package.Category.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                && !package.Description.Contains(term, StringComparison.CurrentCultureIgnoreCase))
            {
                continue;
            }

            Packages.Add(package);
        }

        NotifyListChanged();
    }

    private void NotifyListChanged()
    {
        OnPropertyChanged(nameof(HasPackages));
        OnPropertyChanged(nameof(ShowCenterBusy));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    partial void OnIsBusyChanged(bool value)
        => OnPropertyChanged(nameof(ShowCenterBusy));

    [RelayCommand]
    private void SelectRecommended()
    {
        foreach (var package in Packages)
            package.IsSelected = !package.IsInstalled && package.Recommended;

        StatusMessage = $"추천 항목 {Packages.Count(p => p.IsSelected)}개를 선택했습니다.";
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var package in Packages)
            package.IsSelected = false;

        StatusMessage = null;
    }

    [RelayCommand]
    private async Task InstallSelectedAsync(CancellationToken ct)
    {
        if (IsBusy) return;

        if (!WingetAvailable)
        {
            StatusMessage = "winget 이 없어 설치할 수 없습니다.";
            return;
        }

        var targets = Packages.Where(p => p.IsSelected && !p.IsInstalled).ToList();
        if (targets.Count == 0)
        {
            StatusMessage = "설치할 프로그램을 선택하세요.";
            return;
        }

        var names = string.Join("\n· ", targets.Select(t => t.DisplayName));
        var confirmed = await _dialog.ConfirmAsync(
            "프로그램 설치",
            $"다음 {targets.Count}개 프로그램을 winget 으로 설치합니다.\n\n· {names}\n\n"
            + "일부 항목은 설치 중 관리자 권한(UAC)을 요청할 수 있습니다.",
            "설치");

        if (!confirmed) return;

        IsBusy = true;
        var ok = 0;
        var fail = 0;

        try
        {
            foreach (var package in targets)
            {
                ct.ThrowIfCancellationRequested();

                package.IsBusy = true;
                package.LastError = null;
                StatusMessage = $"설치 중: {package.DisplayName}...";

                try
                {
                    await _winget.InstallAsync(package, ct);
                    ok++;

                    var catalogItem = _catalog.FirstOrDefault(c =>
                        string.Equals(c.Id, package.Id, StringComparison.OrdinalIgnoreCase));
                    if (catalogItem is not null)
                        catalogItem.IsInstalled = true;
                }
                catch (Exception ex)
                {
                    // InstallAsync 가 종료 코드만으로 실패 처리했어도, 실제로 깔렸으면 성공으로 본다.
                    await _winget.RefreshInstalledStateAsync([package], ct);
                    if (package.IsInstalled)
                    {
                        ok++;
                        package.LastError = null;
                        package.IsSelected = false;
                        var catalogItem = _catalog.FirstOrDefault(c =>
                            string.Equals(c.Id, package.Id, StringComparison.OrdinalIgnoreCase));
                        if (catalogItem is not null)
                            catalogItem.IsInstalled = true;
                    }
                    else
                    {
                        fail++;
                        package.LastError = ex.Message;
                    }
                }
                finally
                {
                    package.IsBusy = false;
                }
            }

            ApplyVisibleList();
            StatusMessage = fail == 0
                ? $"{ok}개 설치 완료"
                : $"{ok}개 설치 · {fail}개 실패";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "설치가 취소되었습니다.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string SanitizeWingetError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "알 수 없는 오류";

        // 흔한 winget 소스 오류는 한글 안내로 치환
        if (message.Contains("0x800f024b", StringComparison.OrdinalIgnoreCase)
            || message.Contains("hash for the file is not present", StringComparison.OrdinalIgnoreCase)
            || message.Contains("source reset", StringComparison.OrdinalIgnoreCase))
        {
            return "winget 소스에 문제가 있습니다. 관리자 터미널에서 "
                   + "`winget source reset --force` 후 다시 검색하세요.";
        }

        // 제어 문자·깨진 대체문자 정리
        var cleaned = new string(message
            .Where(ch => !char.IsControl(ch) || ch is '\r' or '\n' or '\t')
            .ToArray())
            .Replace('\uFFFD', ' ')
            .Trim();

        return string.IsNullOrWhiteSpace(cleaned) ? "알 수 없는 오류" : cleaned;
    }

    [RelayCommand]
    private Task OpenWingetHelpAsync()
        => _shell.OpenUrlAsync("ms-windows-store://pdp/?ProductId=9NBLGGH4NNS1");
}
