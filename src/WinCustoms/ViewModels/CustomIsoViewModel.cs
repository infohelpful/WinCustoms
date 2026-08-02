using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCustoms.Common;
using WinCustoms.Models;
using WinCustoms.Services;
using WinCustoms.Services.Catalog;

namespace WinCustoms.ViewModels;

public sealed partial class CustomIsoSelectableTweak : ObservableObject
{
    public required TweakItem Tweak { get; init; }

    public string Title => Tweak.Title;
    public string CategoryLabel => Tweak.Category switch
    {
        TweakCategory.Explorer => "탐색기",
        TweakCategory.Taskbar => "작업 표시줄",
        TweakCategory.Privacy => "개인정보",
        TweakCategory.Performance => "성능",
        TweakCategory.PowerTools => "파워유저",
        _ => Tweak.Category.ToString()
    };

    public bool SupportsIso => Tweak.SupportsOfflineImage;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

public sealed partial class CustomIsoTweakGroup : ObservableObject
{
    public required string Title { get; init; }

    public ObservableCollection<CustomIsoSelectableTweak> Items { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public string Summary
    {
        get
        {
            var selectable = Items.Count(i => i.SupportsIso);
            if (selectable == 0)
                return $"{Items.Count}개 · ISO 미지원";

            var selected = Items.Count(i => i.IsSelected && i.SupportsIso);
            return $"{selected}/{selectable} 선택";
        }
    }

    public void NotifySummary() => OnPropertyChanged(nameof(Summary));
}

public sealed partial class CustomIsoViewModel : ObservableObject
{
    private readonly ICustomIsoService _iso;
    private readonly ITweakCatalog _catalog;
    private readonly IAppxService _appx;
    private readonly IDialogService _dialog;
    private readonly IShellService _shell;
    private CancellationTokenSource? _cts;

    public CustomIsoViewModel(
        ICustomIsoService iso,
        ITweakCatalog catalog,
        IAppxService appx,
        IDialogService dialog,
        IShellService shell)
    {
        _iso = iso;
        _catalog = catalog;
        _appx = appx;
        _dialog = dialog;
        _shell = shell;

        SourceIsoPath = string.Empty;
        OutputIsoPath = string.Empty;
        StatusMessage = "적법하게 보유한 순정 Windows 11 ISO가 필요합니다. 빌드에는 Windows ADK(oscdimg)가 필요합니다.";

        var allTweaks = new List<CustomIsoSelectableTweak>();
        foreach (var tweak in _catalog.All.Where(t => t.Kind == TweakKind.Toggle))
        {
            var item = new CustomIsoSelectableTweak
            {
                Tweak = tweak,
                IsSelected = tweak.SupportsOfflineImage && tweak.Category is
                    TweakCategory.Explorer or TweakCategory.Taskbar or TweakCategory.Privacy or TweakCategory.Performance
            };
            item.PropertyChanged += OnTweakPropertyChanged;
            allTweaks.Add(item);
            Tweaks.Add(item);
        }

        BuildTweakGroups(allTweaks);

        foreach (var pkg in _appx.GetRemovalCatalog())
        {
            pkg.PropertyChanged += OnDebloatPropertyChanged;
            DebloatPackages.Add(pkg);
        }

        OscdimgAvailable = _iso.FindOscdimgPath() is not null;
        if (!OscdimgAvailable)
            StatusMessage = "oscdimg.exe 없음 — Windows ADK Deployment Tools 설치 후 다시 시도하세요.";
    }

    public string Title => "커스텀 ISO";

    public string Subtitle =>
        "순정 Windows 11 ISO에 탐색기·작업표시줄·개인정보·성능 트윅과 기본 앱 제거를 이식해, "
        + "클린 설치용 커스텀 ISO를 만듭니다.";

    /// <summary>플랫 목록(빌드 시 선택 집계용).</summary>
    public ObservableCollection<CustomIsoSelectableTweak> Tweaks { get; } = [];

    public ObservableCollection<CustomIsoTweakGroup> TweakGroups { get; } = [];
    public ObservableCollection<AppxPackageInfo> DebloatPackages { get; } = [];
    public ObservableCollection<WindowsImageInfo> Editions { get; } = [];
    public ObservableCollection<string> LogLines { get; } = [];

    [ObservableProperty]
    public partial string SourceIsoPath { get; set; }

    [ObservableProperty]
    public partial string OutputIsoPath { get; set; }

    [ObservableProperty]
    public partial WindowsImageInfo? SelectedEdition { get; set; }

    [ObservableProperty]
    public partial bool OscdimgAvailable { get; set; }

    /// <summary>TPM / Secure Boot / CPU / RAM / 저장소 등 Win11 설치 검사 우회.</summary>
    [ObservableProperty]
    public partial bool BypassSetupRequirements { get; set; } = true;

    /// <summary>이 PC에 설치된 드라이버를 ISO(install/boot.wim)에 주입.</summary>
    [ObservableProperty]
    public partial bool InjectHostDrivers { get; set; }

    [ObservableProperty]
    public partial bool IsDebloatExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial int ProgressPercent { get; set; }

    [ObservableProperty]
    public partial bool IsProgressIndeterminate { get; set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    public bool CanCancel => IsBusy;
    public bool HasLogLines => LogLines.Count > 0;
    public bool ShowProgressPanel => IsBusy || HasLogLines;

    public string ProgressText => IsProgressIndeterminate || ProgressPercent <= 0
        ? (IsBusy ? "진행 중..." : string.Empty)
        : $"{ProgressPercent}%";

    public string TweaksSummary
    {
        get
        {
            var selectable = Tweaks.Count(t => t.SupportsIso);
            var selected = Tweaks.Count(t => t.IsSelected && t.SupportsIso);
            return $"전체 {selected}/{selectable}개 선택";
        }
    }

    public string DebloatSummary
    {
        get
        {
            var selected = DebloatPackages.Count(p => p.IsSelected);
            var baseText = $"{selected}/{DebloatPackages.Count}개 선택";
            return IsDebloatExpanded ? baseText : baseText + " · 눌러서 목록 보기";
        }
    }

    partial void OnIsDebloatExpandedChanged(bool value)
        => OnPropertyChanged(nameof(DebloatSummary));

    private void BuildTweakGroups(List<CustomIsoSelectableTweak> all)
    {
        TweakGroups.Clear();

        void AddGroup(string title, Func<CustomIsoSelectableTweak, bool> pred, bool expanded)
        {
            var items = all.Where(pred).ToList();
            if (items.Count == 0) return;

            var group = new CustomIsoTweakGroup
            {
                Title = title,
                IsExpanded = expanded
            };
            foreach (var item in items)
                group.Items.Add(item);
            TweakGroups.Add(group);
        }

        AddGroup("탐색기", t => t.Tweak.Category == TweakCategory.Explorer && t.SupportsIso, expanded: false);
        AddGroup("작업 표시줄", t => t.Tweak.Category == TweakCategory.Taskbar && t.SupportsIso, expanded: false);
        AddGroup("개인정보", t => t.Tweak.Category == TweakCategory.Privacy && t.SupportsIso, expanded: false);
        AddGroup("성능", t => t.Tweak.Category == TweakCategory.Performance && t.SupportsIso, expanded: false);
        AddGroup("파워유저", t => t.Tweak.Category == TweakCategory.PowerTools && t.SupportsIso, expanded: false);
        AddGroup("ISO 미지원", t => !t.SupportsIso, expanded: false);

        RefreshSummaries();
    }

    private void OnTweakPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CustomIsoSelectableTweak.IsSelected))
            RefreshSummaries();
    }

    private void OnDebloatPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppxPackageInfo.IsSelected))
            OnPropertyChanged(nameof(DebloatSummary));
    }

    private void RefreshSummaries()
    {
        foreach (var g in TweakGroups)
            g.NotifySummary();
        OnPropertyChanged(nameof(TweaksSummary));
        OnPropertyChanged(nameof(DebloatSummary));
    }

    [RelayCommand]
    private async Task BrowseSourceIsoAsync()
    {
        var path = await _dialog.PickIsoFileAsync();
        if (path is null) return;
        SourceIsoPath = path;
        await LoadEditionsAsync();
    }

    [RelayCommand]
    private async Task BrowseOutputIsoAsync()
    {
        var path = await _dialog.PickSaveIsoAsync($"WinCustoms-Win11-{DateTime.Now:yyyyMMdd}");
        if (path is not null)
            OutputIsoPath = path;
    }

    [RelayCommand]
    private async Task LoadEditionsAsync()
    {
        if (string.IsNullOrWhiteSpace(SourceIsoPath) || !File.Exists(SourceIsoPath))
        {
            StatusMessage = "순정 ISO 경로를 선택하세요.";
            return;
        }

        IsBusy = true;
        StatusMessage = "ISO에서 에디션 목록을 읽는 중...";
        Editions.Clear();
        SelectedEdition = null;
        OnPropertyChanged(nameof(ShowProgressPanel));

        try
        {
            var list = await _iso.ListEditionsAsync(SourceIsoPath);
            foreach (var e in list)
                Editions.Add(e);

            SelectedEdition = Editions.FirstOrDefault(e =>
                                   e.Name.Contains("Pro", StringComparison.OrdinalIgnoreCase))
                               ?? Editions.FirstOrDefault();

            StatusMessage = Editions.Count == 0
                ? "에디션을 찾지 못했습니다."
                : $"에디션 {Editions.Count}개 확인됨.";
        }
        catch (Exception ex)
        {
            StatusMessage = "에디션 읽기 실패: " + ex.Message;
            await _dialog.ShowMessageAsync("ISO 읽기 실패", ex.Message);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanCancel));
            OnPropertyChanged(nameof(ShowProgressPanel));
        }
    }

    [RelayCommand]
    private void SelectRecommendedTweaks()
    {
        foreach (var t in Tweaks)
            t.IsSelected = t.SupportsIso && t.Tweak.Category is
                TweakCategory.Explorer or TweakCategory.Taskbar or TweakCategory.Privacy or TweakCategory.Performance;
        RefreshSummaries();
    }

    [RelayCommand]
    private void SelectRecommendedDebloat()
    {
        foreach (var p in DebloatPackages)
            p.IsSelected = p.RecommendedForRemoval;
        OnPropertyChanged(nameof(DebloatSummary));
    }

    [RelayCommand]
    private void ClearDebloatSelection()
    {
        foreach (var p in DebloatPackages)
            p.IsSelected = false;
        OnPropertyChanged(nameof(DebloatSummary));
    }

    [RelayCommand]
    private async Task OpenAdkHelpAsync()
        => await _shell.OpenUrlAsync("https://learn.microsoft.com/windows-hardware/get-started/adk-install");

    [RelayCommand]
    private async Task BuildAsync()
    {
        if (IsBusy) return;

        OscdimgAvailable = _iso.FindOscdimgPath() is not null;
        if (!OscdimgAvailable)
        {
            StatusMessage = "oscdimg.exe 가 필요합니다.";
            var go = await _dialog.ConfirmAsync(
                "Windows ADK 필요",
                "커스텀 ISO를 만들려면 Windows ADK의 Deployment Tools(oscdimg)가 필요합니다.\n\n설치 안내 페이지를 열까요?",
                "안내 열기");
            if (go) await OpenAdkHelpAsync();
            return;
        }

        if (string.IsNullOrWhiteSpace(SourceIsoPath) || !File.Exists(SourceIsoPath))
        {
            StatusMessage = "순정 ISO를 선택하세요.";
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputIsoPath))
        {
            StatusMessage = "저장할 ISO 경로를 선택하세요.";
            return;
        }

        if (SelectedEdition is null)
        {
            StatusMessage = "에디션(인덱스)을 선택하세요. ISO를 고른 뒤 목록이 채워집니다.";
            return;
        }

        var selectedTweaks = Tweaks.Where(t => t.IsSelected && t.SupportsIso).Select(t => t.Tweak).ToList();
        var selectedApps = DebloatPackages.Where(p => p.IsSelected).ToList();
        var appNames = selectedApps
            .SelectMany(p => p.CandidateNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (selectedTweaks.Count == 0
            && appNames.Count == 0
            && !BypassSetupRequirements
            && !InjectHostDrivers)
        {
            StatusMessage = "트윅·앱 제거·설치 검사 우회·드라이버 주입 중 하나 이상을 선택하세요.";
            return;
        }

        var extras = new List<string>();
        if (BypassSetupRequirements) extras.Add("설치 검사 우회");
        if (InjectHostDrivers) extras.Add("현재 PC 드라이버 주입");

        var confirmed = await _dialog.ConfirmAsync(
            "커스텀 ISO 만들기",
            "순정 ISO를 풀어 설정을 이식한 뒤 새 설치 ISO를 만듭니다.\n\n"
            + $"원본: {SourceIsoPath}\n"
            + $"저장: {OutputIsoPath}\n"
            + $"에디션: {SelectedEdition.DisplayText}\n"
            + $"트윅: {selectedTweaks.Count}개 · 앱 제거: {selectedApps.Count}개\n"
            + (extras.Count > 0 ? $"추가: {string.Join(" · ", extras)}\n" : string.Empty)
            + "\n"
            + "· 수십 GB 여유 공간과 시간이 필요합니다.\n"
            + "· 관리자 권한(UAC)이 필요합니다.\n"
            + "· 결과 ISO는 클린 설치용입니다 (업그레이드 설치는 효과가 제한적일 수 있음).\n"
            + "· 드라이버 주입은 이 PC와 같은(또는 호환) 하드웨어용입니다.\n"
            + "· 순정 ISO는 사용자가 적법하게 보유한 파일이어야 합니다.\n\n"
            + "시작할까요?",
            "ISO 만들기");

        if (!confirmed) return;

        await RunBusyAsync(async (progress, ct) =>
        {
            AppendLog("커스텀 ISO 빌드 시작");
            var result = await _iso.BuildAsync(
                SourceIsoPath,
                OutputIsoPath,
                SelectedEdition.Index,
                selectedTweaks,
                appNames,
                BypassSetupRequirements,
                InjectHostDrivers,
                progress,
                ct);

            if (!result.Success)
                throw new InvalidOperationException(result.Error ?? "빌드 실패");

            AppendLog("완료: " + result.OutputIsoPath);
            StatusMessage = "커스텀 ISO를 만들었습니다.";
            await _dialog.ShowMessageAsync(
                "완료",
                $"커스텀 설치 ISO를 저장했습니다.\n\n{result.OutputIsoPath}\n\n"
                + "USB에 Rufus 등으로 구운 뒤 클린 설치하세요.");
        });
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        StatusMessage = "취소 요청…";
    }

    private async Task RunBusyAsync(Func<IProgress<SystemImageProgressLine>, CancellationToken, Task> work)
    {
        IsBusy = true;
        ProgressPercent = 0;
        IsProgressIndeterminate = true;
        LogLines.Clear();
        NotifyProgressProps();

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var progress = new Progress<SystemImageProgressLine>(line =>
        {
            if (line.Percent is int p)
            {
                ProgressPercent = p;
                IsProgressIndeterminate = false;
                OnPropertyChanged(nameof(ProgressText));
            }

            if (!string.IsNullOrWhiteSpace(line.Message))
            {
                AppendLog(line.Message);
                StatusMessage = line.Message;
            }
        });

        try
        {
            await work(progress, ct);
        }
        catch (ElevationDeniedException)
        {
            StatusMessage = "관리자 권한이 거부되었습니다.";
            AppendLog(StatusMessage);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "취소되었습니다.";
            AppendLog(StatusMessage);
        }
        catch (Exception ex)
        {
            StatusMessage = "실패: " + ex.Message;
            AppendLog(StatusMessage);
            await _dialog.ShowMessageAsync("커스텀 ISO 오류", ex.Message);
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
            _cts.Dispose();
            _cts = null;
            NotifyProgressProps();
        }
    }

    private void AppendLog(string message)
    {
        LogLines.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        while (LogLines.Count > 200)
            LogLines.RemoveAt(0);
        OnPropertyChanged(nameof(HasLogLines));
        OnPropertyChanged(nameof(ShowProgressPanel));
    }

    private void NotifyProgressProps()
    {
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(HasLogLines));
        OnPropertyChanged(nameof(ShowProgressPanel));
    }
}
