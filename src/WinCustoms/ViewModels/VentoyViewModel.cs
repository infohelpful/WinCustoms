using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCustoms.Common;
using WinCustoms.Services;

namespace WinCustoms.ViewModels;

/// <summary>
/// Ventoy 스타일 부팅 USB. 대상 디스크에 Ventoy 를 설치/업데이트하는 것까지만 담당한다.
/// ISO는 만들지 않는다 — 사용자가 "커스텀 ISO 만들기" 메뉴에서 따로 만들어서 이 USB의
/// 데이터 파티션에 직접 복사해 넣는 방식(순정 Ventoy 사용법 그대로). 자세한 설계는
/// docs/ventoy-style-feature-plan.md, docs/ventoy-feature-devplan.md 참고.
/// </summary>
public sealed partial class VentoyViewModel : ObservableObject
{
    private readonly IVentoyService _ventoy;
    private readonly IDialogService _dialog;
    private CancellationTokenSource? _cts;

    public VentoyViewModel(IVentoyService ventoy, IDialogService dialog)
    {
        _ventoy = ventoy;
        _dialog = dialog;

        StatusMessage = "대상 USB를 고르고 Ventoy를 설치하면, 커스텀 ISO를 몇 개든 올려 골라 부팅할 수 있습니다.";

        PartitionSchemeOptions.Add("GPT");
        PartitionSchemeOptions.Add("MBR");
        FileSystemOptions.Add("exFAT (기본)");
        FileSystemOptions.Add("NTFS");
        FileSystemOptions.Add("FAT32");
        FileSystemOptions.Add("UDF");
        SelectedPartitionSchemeOption = "GPT";
        SelectedFileSystemOption = "exFAT (기본)";

        _ = RefreshDisksAsync();
    }

    public string Title => "Ventoy";

    public string Subtitle =>
        "USB 하나에 Ventoy를 설치해 두면, 커스텀 ISO를 몇 개든 올려서 부팅할 때마다 골라 설치할 수 있습니다.";

    public ObservableCollection<VentoyDiskInfo> Disks { get; } = [];
    public ObservableCollection<string> PartitionSchemeOptions { get; } = [];
    public ObservableCollection<string> FileSystemOptions { get; } = [];
    public ObservableCollection<string> LogLines { get; } = [];

    [ObservableProperty]
    public partial VentoyDiskInfo? SelectedDisk { get; set; }

    [ObservableProperty]
    public partial string SelectedPartitionSchemeOption { get; set; } = "GPT";

    [ObservableProperty]
    public partial string SelectedFileSystemOption { get; set; } = "exFAT (기본)";

    [ObservableProperty]
    public partial bool DisableSecureBoot { get; set; }

    /// <summary>기존 Ventoy USB의 다른 ISO 파일들을 지우지 않고 유지(비파괴 재설치/업데이트).</summary>
    [ObservableProperty]
    public partial bool NonDestructive { get; set; } = true;

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

    private VentoyPartitionScheme CurrentPartitionScheme =>
        string.Equals(SelectedPartitionSchemeOption, "MBR", StringComparison.OrdinalIgnoreCase)
            ? VentoyPartitionScheme.Mbr
            : VentoyPartitionScheme.Gpt;

    private VentoyFileSystem CurrentFileSystem => SelectedFileSystemOption switch
    {
        "NTFS" => VentoyFileSystem.Ntfs,
        "FAT32" => VentoyFileSystem.Fat32,
        "UDF" => VentoyFileSystem.Udf,
        _ => VentoyFileSystem.ExFat
    };

    [RelayCommand]
    private async Task RefreshDisksAsync()
    {
        try
        {
            var previousNumber = SelectedDisk?.Number;
            var list = await _ventoy.ListDisksAsync();
            await UiThread.InvokeAsync(() =>
            {
                Disks.Clear();
                foreach (var d in list)
                    Disks.Add(d);

                SelectedDisk = previousNumber is int n
                    ? Disks.FirstOrDefault(d => d.Number == n) ?? Disks.FirstOrDefault()
                    : Disks.FirstOrDefault();

                StatusMessage = Disks.Count == 0
                    ? "USB/외장 디스크를 찾지 못했습니다. 연결하면 새로고침해 주세요."
                    : $"장치 {Disks.Count}개";
            });
        }
        catch (Exception ex)
        {
            await UiThread.InvokeAsync(() => StatusMessage = "디스크 목록 오류: " + ex.Message);
        }
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (SelectedDisk is null)
        {
            StatusMessage = "대상 USB를 먼저 선택하세요.";
            return;
        }

        var isUpdate = SelectedDisk.IsVentoyInstalled;
        var diskDisplay = SelectedDisk.DisplayText;

        var confirmed = await _dialog.ConfirmAsync(
            "Ventoy 설치",
            $"장치: {diskDisplay}\n"
            + $"파티션: {SelectedPartitionSchemeOption} · {SelectedFileSystemOption}\n"
            + (isUpdate
                ? "· 이미 Ventoy가 설치돼 있어 업데이트만 합니다(기존 ISO 파일 유지).\n"
                : "· 이 USB에 Ventoy를 새로 설치합니다" + (NonDestructive ? "(기존 데이터 유지 시도).\n" : ". 기존 데이터가 지워집니다.\n"))
            + "\n계속할까요?",
            "USB 준비");

        if (!confirmed) return;

        var diskNumber = SelectedDisk.Number;
        var diskFriendlyName = SelectedDisk.FriendlyName;
        var diskSizeBytes = SelectedDisk.SizeBytes;

        await RunBusyAsync(async (progress, ct) =>
        {
            AppendLog(isUpdate ? "Ventoy 업데이트 시작" : "Ventoy 설치 시작");

            var installRequest = new VentoyInstallJobRequest
            {
                DiskNumber = diskNumber,
                DiskFriendlyName = diskFriendlyName,
                DiskSizeBytes = diskSizeBytes,
                PartitionScheme = CurrentPartitionScheme,
                FileSystem = CurrentFileSystem,
                IsUpdate = isUpdate,
                NonDestructive = isUpdate || NonDestructive,
                DisableSecureBoot = DisableSecureBoot
            };

            var installResult = await _ventoy.InstallAsync(installRequest, progress, ct).ConfigureAwait(false);
            if (!installResult.Success)
                throw new InvalidOperationException(installResult.Error ?? "Ventoy 설치 실패");

            ProgressPercent = 100;
            IsProgressIndeterminate = false;
            OnPropertyChanged(nameof(ProgressText));
            AppendLog($"완료 ({installResult.DataDriveLetter}:)");
            StatusMessage = "Ventoy 설치가 끝났습니다.";
            await _dialog.ShowMessageAsync(
                "완료",
                "Ventoy 설치가 끝났습니다.\n\n"
                + diskDisplay
                + (installResult.DataDriveLetter is { Length: > 0 } letter ? $" ({letter}:)" : string.Empty)
                + "\n\n이제 \"커스텀 ISO 만들기\"에서 원하는 대로 ISO를 만든 뒤, 그 파티션에 파일로 복사해 넣으세요. "
                + "USB 하나에 여러 개를 올려도 되고, 부팅 시 메뉴에서 골라 설치할 수 있습니다.");
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
            var msg = line.Message.TrimStart('​');
            StatusMessage = msg;
            AppendLog(msg);
        });

        try
        {
            await work(progress, ct);
        }
        catch (ElevationDeniedException)
        {
            await UiThread.InvokeAsync(() =>
            {
                StatusMessage = "관리자 권한이 거부되었습니다.";
                AppendLog(StatusMessage);
            });
        }
        catch (OperationCanceledException)
        {
            await UiThread.InvokeAsync(() =>
            {
                StatusMessage = "취소되었습니다.";
                AppendLog(StatusMessage);
            });
        }
        catch (Exception ex)
        {
            await UiThread.InvokeAsync(() =>
            {
                StatusMessage = "실패: " + ex.Message;
                AppendLog(StatusMessage);
            });
            await _dialog.ShowMessageAsync("Ventoy USB 오류", ex.Message);
        }
        finally
        {
            // work(progress, ct) 내부 서비스 체인이 ConfigureAwait(false)를 쓰기 때문에
            // 여기가 UI 스레드가 아닐 수 있다 — IsBusy 를 그대로 바꾸면 NavView.IsEnabled 등
            // XAML 요소를 다른 스레드에서 건드려 RPC_E_WRONG_THREAD 로 즉시 프로세스가 죽는다
            // (Microsoft.UI.Windowing.dll fail-fast, 실측 확인됨). 반드시 UiThread 를 거친다.
            await UiThread.InvokeAsync(() =>
            {
                IsBusy = false;
                IsProgressIndeterminate = false;
                NotifyProgressProps();
            });
            _cts.Dispose();
            _cts = null;
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
}
