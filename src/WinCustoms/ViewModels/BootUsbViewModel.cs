using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windows.Devices.Enumeration;
using WinCustoms.Common;
using WinCustoms.Models;
using WinCustoms.Services;
using WinCustoms.Services.Catalog;

namespace WinCustoms.ViewModels;

public sealed partial class BootUsbViewModel : ObservableObject
{
    /// <summary>GUID_DEVINTERFACE_DISK — USB 꽂고 빼는 걸 잡기에 적합.</summary>
    private const string DiskInterfaceAqs =
        @"System.Devices.InterfaceClassGuid:=""{53F56307-B6BF-11D0-94F2-00A0C91EFB8B}""";

    private readonly IBootUsbService _bootUsb;
    private readonly ITweakCatalog _catalog;
    private readonly IAppxService _appx;
    private readonly IDialogService _dialog;
    private CancellationTokenSource? _cts;
    private DeviceWatcher? _diskWatcher;
    private CancellationTokenSource? _deviceDebounceCts;
    private int _deviceWatchRefCount;

    public BootUsbViewModel(
        IBootUsbService bootUsb,
        ITweakCatalog catalog,
        IAppxService appx,
        IDialogService dialog)
    {
        _bootUsb = bootUsb;
        _catalog = catalog;
        _appx = appx;
        _dialog = dialog;

        SourceIsoPath = string.Empty;
        VolumeLabel = "WIN11";
        StatusMessage = "USB/외장 디스크를 고른 뒤 순정 Windows ISO로 부팅 USB를 만들 수 있습니다.";

        PartitionSchemeOptions.Add("GPT");
        PartitionSchemeOptions.Add("MBR");
        FileSystemOptions.Add("FAT32");
        FileSystemOptions.Add("NTFS");
        ClusterSizeOptions.Add("기본값");
        ClusterSizeOptions.Add("4096 바이트");
        ClusterSizeOptions.Add("8192 바이트");
        ClusterSizeOptions.Add("16 KB");
        ClusterSizeOptions.Add("32 KB");
        SelectedPartitionSchemeOption = "GPT";
        SelectedFileSystemOption = "NTFS";
        SelectedClusterSizeOption = "기본값";
        QuickFormat = true;
        CreateExtendedLabelAndIcon = true;
        ShowOptimizationOptions = false;
        // 최적화 설정 ON 시 기본값 (숨김 상태에서는 작성에 반영하지 않음)
        BypassSetupRequirements = true;
        SkipOnlineAccount = true;
        SkipPrivacyExperience = true;
        LocalAccountName = "admin";

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

        _ = RefreshDisksAsync();
    }

    public string Title => "부팅 USB";

    public string Subtitle =>
        "Rufus처럼 USB/외장 디스크에 Windows 설치 미디어를 만듭니다. "
        + "기본은 순정 그대로이며, 최적화 설정을 켜면 트윅·앱 제거·OOBE를 적용할 수 있습니다.";

    public ObservableCollection<BootUsbDiskInfo> Disks { get; } = [];
    public ObservableCollection<string> PartitionSchemeOptions { get; } = [];
    public ObservableCollection<string> FileSystemOptions { get; } = [];
    public ObservableCollection<string> ClusterSizeOptions { get; } = [];
    public ObservableCollection<CustomIsoSelectableTweak> Tweaks { get; } = [];
    public ObservableCollection<CustomIsoTweakGroup> TweakGroups { get; } = [];
    public ObservableCollection<AppxPackageInfo> DebloatPackages { get; } = [];
    public ObservableCollection<WindowsImageInfo> Editions { get; } = [];
    public ObservableCollection<string> LogLines { get; } = [];

    [ObservableProperty]
    public partial BootUsbDiskInfo? SelectedDisk { get; set; }

    [ObservableProperty]
    public partial string SourceIsoPath { get; set; }

    [ObservableProperty]
    public partial WindowsImageInfo? SelectedEdition { get; set; }

    [ObservableProperty]
    public partial string SelectedPartitionSchemeOption { get; set; } = "GPT";

    [ObservableProperty]
    public partial string SelectedFileSystemOption { get; set; } = "FAT32";

    [ObservableProperty]
    public partial string VolumeLabel { get; set; }

    [ObservableProperty]
    public partial string SelectedClusterSizeOption { get; set; } = "기본값";

    [ObservableProperty]
    public partial bool QuickFormat { get; set; }

    [ObservableProperty]
    public partial bool CreateExtendedLabelAndIcon { get; set; }

    /// <summary>켜면 4~6(트윅·디블로트·추가 옵션)을 표시하고 작성 시 반영합니다. 기본 OFF=순정.</summary>
    [ObservableProperty]
    public partial bool ShowOptimizationOptions { get; set; }

    [ObservableProperty]
    public partial bool BypassSetupRequirements { get; set; }

    [ObservableProperty]
    public partial bool InjectHostDrivers { get; set; }

    [ObservableProperty]
    public partial bool SkipOnlineAccount { get; set; }

    [ObservableProperty]
    public partial bool SkipPrivacyExperience { get; set; }

    [ObservableProperty]
    public partial string LocalAccountName { get; set; }

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

    public string TargetSystemText =>
        string.Equals(SelectedPartitionSchemeOption, "MBR", StringComparison.OrdinalIgnoreCase)
            ? "BIOS 또는 UEFI-CSM"
            : "UEFI (CSM 미지원)";

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

    partial void OnSelectedPartitionSchemeOptionChanged(string value)
        => OnPropertyChanged(nameof(TargetSystemText));

    private BootUsbPartitionScheme CurrentPartitionScheme =>
        string.Equals(SelectedPartitionSchemeOption, "MBR", StringComparison.OrdinalIgnoreCase)
            ? BootUsbPartitionScheme.Mbr
            : BootUsbPartitionScheme.Gpt;

    private BootUsbFileSystem CurrentFileSystem =>
        string.Equals(SelectedFileSystemOption, "NTFS", StringComparison.OrdinalIgnoreCase)
            ? BootUsbFileSystem.Ntfs
            : BootUsbFileSystem.Fat32;

    private int CurrentClusterSize => SelectedClusterSizeOption switch
    {
        "4096 바이트" => 4096,
        "8192 바이트" => 8192,
        "16 KB" => 16384,
        "32 KB" => 32768,
        _ => 0
    };

    partial void OnIsDebloatExpandedChanged(bool value)
        => OnPropertyChanged(nameof(DebloatSummary));

    private void BuildTweakGroups(List<CustomIsoSelectableTweak> all)
    {
        TweakGroups.Clear();

        void AddGroup(string title, Func<CustomIsoSelectableTweak, bool> pred, bool expanded)
        {
            var items = all.Where(pred).ToList();
            if (items.Count == 0) return;
            var group = new CustomIsoTweakGroup { Title = title, IsExpanded = expanded };
            foreach (var item in items) group.Items.Add(item);
            TweakGroups.Add(group);
        }

        AddGroup("탐색기", t => t.Tweak.Category == TweakCategory.Explorer && t.SupportsIso, false);
        AddGroup("작업 표시줄", t => t.Tweak.Category == TweakCategory.Taskbar && t.SupportsIso, false);
        AddGroup("개인정보", t => t.Tweak.Category == TweakCategory.Privacy && t.SupportsIso, false);
        AddGroup("성능", t => t.Tweak.Category == TweakCategory.Performance && t.SupportsIso, false);
        AddGroup("파워유저", t => t.Tweak.Category == TweakCategory.PowerTools && t.SupportsIso, false);
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
        foreach (var g in TweakGroups) g.NotifySummary();
        OnPropertyChanged(nameof(TweaksSummary));
        OnPropertyChanged(nameof(DebloatSummary));
    }

    [RelayCommand]
    private async Task RefreshDisksAsync()
    {
        try
        {
            var previousNumber = SelectedDisk?.Number;
            var list = await _bootUsb.ListDisksAsync();
            await UiThread.InvokeAsync(() =>
            {
                Disks.Clear();
                foreach (var d in list)
                    Disks.Add(d);

                SelectedDisk = previousNumber is int n
                    ? Disks.FirstOrDefault(d => d.Number == n) ?? Disks.FirstOrDefault()
                    : Disks.FirstOrDefault();

                StatusMessage = Disks.Count == 0
                    ? "USB/외장 디스크를 찾지 못했습니다. 연결하면 자동으로 목록이 갱신됩니다."
                    : $"장치 {Disks.Count}개";
            });
        }
        catch (Exception ex)
        {
            await UiThread.InvokeAsync(() => StatusMessage = "디스크 목록 오류: " + ex.Message);
        }
    }

    /// <summary>페이지 진입 시 호출. USB 연결/해제 시 목록 자동 갱신.</summary>
    public void StartDeviceWatch()
    {
        _deviceWatchRefCount++;
        if (_diskWatcher is not null)
            return;

        try
        {
            var watcher = DeviceInformation.CreateWatcher(DiskInterfaceAqs);
            watcher.Added += OnDiskDeviceChanged;
            watcher.Removed += OnDiskDeviceChanged;
            watcher.Updated += OnDiskDeviceChanged;
            watcher.EnumerationCompleted += OnDiskEnumerationCompleted;
            _diskWatcher = watcher;
            watcher.Start();
        }
        catch (Exception ex)
        {
            StatusMessage = "장치 감시 시작 실패: " + ex.Message;
        }
    }

    /// <summary>페이지 이탈 시 호출.</summary>
    public void StopDeviceWatch()
    {
        if (_deviceWatchRefCount > 0)
            _deviceWatchRefCount--;
        if (_deviceWatchRefCount > 0)
            return;

        _deviceDebounceCts?.Cancel();
        _deviceDebounceCts?.Dispose();
        _deviceDebounceCts = null;

        var watcher = _diskWatcher;
        _diskWatcher = null;
        if (watcher is null)
            return;

        try
        {
            watcher.Added -= OnDiskDeviceChanged;
            watcher.Removed -= OnDiskDeviceChanged;
            watcher.Updated -= OnDiskDeviceChanged;
            watcher.EnumerationCompleted -= OnDiskEnumerationCompleted;
            if (watcher.Status is DeviceWatcherStatus.Started
                or DeviceWatcherStatus.EnumerationCompleted)
                watcher.Stop();
        }
        catch
        {
            // ignore
        }
    }

    private void OnDiskDeviceChanged(DeviceWatcher sender, DeviceInformation update)
        => ScheduleDeviceRefresh();

    private void OnDiskDeviceChanged(DeviceWatcher sender, DeviceInformationUpdate update)
        => ScheduleDeviceRefresh();

    private void OnDiskEnumerationCompleted(DeviceWatcher sender, object args)
        => ScheduleDeviceRefresh();

    private void ScheduleDeviceRefresh()
    {
        if (IsBusy)
            return;

        _deviceDebounceCts?.Cancel();
        _deviceDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _deviceDebounceCts = cts;
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                // 한 번에 이벤트가 여러 번 오므로 짧게 모아서 한 번만 새로고침
                await Task.Delay(700, token).ConfigureAwait(false);
                if (token.IsCancellationRequested || IsBusy)
                    return;

                await UiThread.InvokeAsync(async () =>
                {
                    if (!IsBusy)
                        await RefreshDisksAsync();
                });
            }
            catch (OperationCanceledException)
            {
                // debounce
            }
            catch
            {
                // ignore watcher noise
            }
        }, token);
    }

    [RelayCommand]
    private async Task BrowseIsoAsync()
    {
        var path = await _dialog.PickIsoFileAsync();
        if (path is null) return;
        SourceIsoPath = path;
        await LoadEditionsAsync();
    }

    [RelayCommand]
    private void OpenWindows11Download()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = BootUsbJobHost.Windows11DownloadUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = "다운로드 페이지를 열지 못했습니다: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task LoadEditionsAsync()
    {
        if (string.IsNullOrWhiteSpace(SourceIsoPath) || !File.Exists(SourceIsoPath))
        {
            StatusMessage = "ISO 경로를 선택하세요.";
            return;
        }

        IsBusy = true;
        StatusMessage = "ISO 에디션 읽는 중…";
        Editions.Clear();
        SelectedEdition = null;

        try
        {
            var list = await _bootUsb.ListEditionsAsync(SourceIsoPath);
            foreach (var e in list) Editions.Add(e);
            // 순정 굽기만 할 수도 있어서 강제 선택하지 않음. 트윅 쓸 때 고르거나 작성 시 자동 보정.
            SelectedEdition = null;
            StatusMessage = Editions.Count == 0
                ? "에디션 없음"
                : $"에디션 {Editions.Count}개 (트윅·옵션 넣을 때만 선택)";
        }
        catch (ElevationDeniedException)
        {
            StatusMessage = "관리자 권한이 필요합니다.";
            await _dialog.ShowMessageAsync("권한 필요", "에디션을 읽으려면 UAC 승인이 필요합니다.");
        }
        catch (Exception ex)
        {
            StatusMessage = "ISO 읽기 실패: " + ex.Message;
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
    private void ClearTweaksSelection()
    {
        foreach (var t in Tweaks)
            t.IsSelected = false;
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
    private async Task CreateAsync()
    {
        if (IsBusy) return;

        if (SelectedDisk is null)
        {
            StatusMessage = "USB/외장 장치를 선택하세요.";
            return;
        }

        if (string.IsNullOrWhiteSpace(SourceIsoPath) || !File.Exists(SourceIsoPath))
        {
            StatusMessage = "Windows ISO를 선택하세요.";
            return;
        }

        var optimize = ShowOptimizationOptions;
        var localName = optimize ? (LocalAccountName ?? string.Empty).Trim() : string.Empty;
        if (optimize)
        {
            var accountError = CustomIsoUnattend.ValidateAccountName(localName);
            if (accountError is not null)
            {
                await _dialog.ShowMessageAsync("계정 이름", accountError);
                return;
            }
        }

        var selectedTweaks = optimize
            ? Tweaks.Where(t => t.IsSelected && t.SupportsIso).Select(t => t.Tweak).ToList()
            : [];
        var selectedApps = optimize
            ? DebloatPackages.Where(p => p.IsSelected).ToList()
            : [];
        var appNames = selectedApps.SelectMany(p => p.CandidateNames).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var applyBypass = optimize && BypassSetupRequirements;
        var applyDrivers = optimize && InjectHostDrivers;
        var applySkipAccount = optimize && SkipOnlineAccount;
        var applySkipPrivacy = optimize && SkipPrivacyExperience;

        // install.wim 을 마운트해서 손볼 때만 에디션이 필요. 순정 구우면 설치 화면에서 고르면 됨.
        var needsEdition = selectedTweaks.Count > 0
                           || appNames.Count > 0
                           || applyBypass
                           || applyDrivers
                           || applySkipAccount
                           || applySkipPrivacy
                           || localName.Length > 0;

        WindowsImageInfo? edition = SelectedEdition;
        if (needsEdition)
        {
            edition ??= Editions.FirstOrDefault(e =>
                            e.Name.Contains("Pro", StringComparison.OrdinalIgnoreCase))
                        ?? Editions.FirstOrDefault();
            if (edition is null)
            {
                StatusMessage = "트윅·옵션 적용을 위해 ISO 에디션 목록을 먼저 읽으세요.";
                await _dialog.ShowMessageAsync(
                    "에디션 필요",
                    "최적화 설정(트윅·앱 제거·추가 옵션)을 쓸 때는 적용할 에디션이 필요합니다.\n\n"
                    + "ISO를 고른 뒤 에디션 목록이 채워졌는지 확인하거나, 순정만 구울 거면 「최적화 설정」을 끄세요.");
                return;
            }
        }

        var imageIndex = edition?.Index ?? 1;

        var confirmed = await _dialog.ConfirmAsync(
            "부팅 USB 만들기",
            "선택한 디스크의 모든 파티션·데이터가 삭제됩니다.\n\n"
            + $"장치: {SelectedDisk.DisplayText}\n"
            + $"ISO: {SourceIsoPath}\n"
            + (edition is not null
                ? $"에디션: {edition.DisplayText}\n"
                : "에디션: 순정 그대로(설치 시 선택)\n")
            + $"모드: {(optimize ? "최적화 설정 적용" : "순정 그대로")}\n"
            + $"파티션: {SelectedPartitionSchemeOption} · {TargetSystemText}\n"
            + $"파일 시스템: {SelectedFileSystemOption} · 레이블: {VolumeLabel}\n\n"
            + "· 잘못된 디스크를 고르면 복구하기 어렵습니다.\n"
            + "· 관리자 권한(UAC)이 필요합니다.\n\n"
            + "계속할까요?",
            "디스크 지우기 및 작성");

        if (!confirmed) return;

        var template = new BootUsbJobRequest
        {
            SourceIsoPath = SourceIsoPath,
            ImageIndex = imageIndex,
            DiskNumber = SelectedDisk.Number,
            DiskFriendlyName = SelectedDisk.FriendlyName,
            DiskSizeBytes = SelectedDisk.SizeBytes,
            PartitionScheme = CurrentPartitionScheme,
            FileSystem = CurrentFileSystem,
            VolumeLabel = VolumeLabel,
            ClusterSizeBytes = CurrentClusterSize,
            QuickFormat = QuickFormat,
            CreateExtendedLabelAndIcon = CreateExtendedLabelAndIcon,
            BypassSetupRequirements = applyBypass,
            InjectHostDrivers = applyDrivers,
            SkipOnlineAccount = applySkipAccount,
            SkipPrivacyExperience = applySkipPrivacy,
            LocalAccountName = localName
        };

        await RunBusyAsync(async (progress, ct) =>
        {
            AppendLog("부팅 USB 작성 시작");
            var result = await _bootUsb.CreateAsync(template, selectedTweaks, appNames, progress, ct);
            if (!result.Success)
                throw new InvalidOperationException(result.Error ?? "작성 실패");

            ProgressPercent = 100;
            IsProgressIndeterminate = false;
            OnPropertyChanged(nameof(ProgressText));
            AppendLog("완료: " + (result.TargetDescription ?? "USB"));
            StatusMessage = "부팅 USB를 만들었습니다.";
            await _dialog.ShowMessageAsync(
                "완료",
                "부팅 USB 작성이 끝났습니다.\n\n"
                + (result.TargetDescription ?? string.Empty)
                + "\n\nPC를 USB로 부팅해 Windows를 설치하세요.");
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
                if (p >= ProgressPercent || IsProgressIndeterminate)
                {
                    ProgressPercent = p;
                    IsProgressIndeterminate = false;
                    OnPropertyChanged(nameof(ProgressText));
                }
            }

            if (string.IsNullOrWhiteSpace(line.Message)) return;
            var msg = line.Message.TrimStart('\u200B');
            StatusMessage = msg;
            if (!IsStatusOnlyMessage(line.Message))
                AppendLog(msg);
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
            await _dialog.ShowMessageAsync("부팅 USB 오류", ex.Message);
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
        while (LogLines.Count > 200) LogLines.RemoveAt(0);
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

    private static bool IsStatusOnlyMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return true;
        if (message.StartsWith('\u200B')) return true;
        var msg = message.TrimStart('\u200B');
        if (msg.Contains("경과 ", StringComparison.Ordinal)) return true;
        if (Regex.IsMatch(msg, @"^\S+\.exe\s+\d{1,3}%\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return true;
        return false;
    }
}
