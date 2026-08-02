using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCustoms.Common;
using WinCustoms.Services;

namespace WinCustoms.ViewModels;

public sealed partial class SystemBackupViewModel : ObservableObject
{
    private readonly ISystemImageService _images;
    private readonly IDialogService _dialog;
    private CancellationTokenSource? _cts;

    public SystemBackupViewModel(ISystemImageService images, IDialogService dialog)
    {
        _images = images;
        _dialog = dialog;

        ImageName = $"WinCustoms {DateTime.Now:yyyy-MM-dd}";
        ImageFilePath = string.Empty;
        RestoreImagePath = string.Empty;
        StatusMessage = "C: 백업 후 「C: 자동 복원」을 누르면, 다시 시작과 함께 복원이 자동으로 시작됩니다.";
    }

    public string Title => "시스템 백업";

    public string Subtitle =>
        "현재 C: Windows를 .wim 으로 복사해 둡니다. "
        + "「C: 자동 복원」을 누르면 컴퓨터가 다시 시작된 뒤, 백업 시점으로 복원이 자동 진행됩니다.";

    public ObservableCollection<string> LogLines { get; } = [];

    [ObservableProperty]
    public partial string ImageName { get; set; }

    [ObservableProperty]
    public partial string ImageFilePath { get; set; }

    [ObservableProperty]
    public partial string RestoreImagePath { get; set; }

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

    [RelayCommand]
    private async Task BrowseSavePathAsync()
    {
        var path = await _dialog.PickSaveWimAsync($"WinCustoms-Backup-{DateTime.Now:yyyyMMdd}");
        if (path is not null)
            ImageFilePath = path;
    }

    [RelayCommand]
    private async Task BrowseRestoreImageAsync()
    {
        var path = await _dialog.PickWimFileAsync();
        if (path is not null)
            RestoreImagePath = path;
    }

    [RelayCommand]
    private async Task CaptureAsync()
    {
        if (IsBusy) return;

        if (string.IsNullOrWhiteSpace(ImageFilePath))
        {
            StatusMessage = "저장할 WIM 경로를 선택하세요. (외장 드라이브 권장)";
            return;
        }

        var systemRoot = _images.GetSystemVolumeRoot();
        var imageRoot = Path.GetPathRoot(ImageFilePath);
        if (imageRoot is not null
            && string.Equals(imageRoot.TrimEnd('\\'), systemRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            var proceed = await _dialog.ConfirmAsync(
                "시스템 드라이브에 저장",
                "백업 파일을 C:에 저장하면 C:가 망가졌을 때 백업에도 손이 안 닿을 수 있습니다.\n"
                + "USB/외장 SSD에 저장하세요.\n\n계속할까요?",
                "계속 저장");
            if (!proceed) return;
        }

        var confirmed = await _dialog.ConfirmAsync(
            "C: Windows 백업",
            "지금 이 PC의 Windows 전체를 .wim 으로 복사합니다.\n\n"
            + $"저장: {ImageFilePath}\n\n"
            + "· 수십 GB, 오래 걸릴 수 있습니다.\n"
            + "· 관리자 권한(UAC)이 필요합니다.\n"
            + "· 같은 PC 복구용입니다.",
            "백업 시작");

        if (!confirmed) return;

        await RunOperationAsync(async (progress, ct) =>
        {
            AppendLog($"C: 캡처 시작 → {ImageFilePath}");
            var result = await _images.CaptureAsync(ImageFilePath, ImageName, progress, ct);
            if (!result.Success)
                throw new InvalidOperationException(result.Error ?? "캡처에 실패했습니다.");

            RestoreImagePath = result.ImageFile ?? ImageFilePath;
            AppendLog($"완료: {result.ImageFile}");
            StatusMessage = "백업 완료. 아래에서 「C: 자동 복원」으로 되돌릴 수 있습니다.";

            await _dialog.ShowMessageAsync(
                "백업 완료",
                $"저장됨:\n{result.ImageFile}\n\n"
                + "복원할 때: WIM 선택 → 「C: 자동 복원」→ 다시 시작되면 자동으로 적용됩니다.\n"
                + "USB는 꽂아 두세요.");
        });
    }

    [RelayCommand]
    private async Task PrepareRestoreAsync()
    {
        if (IsBusy) return;

        if (string.IsNullOrWhiteSpace(RestoreImagePath) || !File.Exists(RestoreImagePath))
        {
            StatusMessage = "복원할 WIM 파일을 선택하세요.";
            return;
        }

        var systemRoot = _images.GetSystemVolumeRoot();
        var imageRoot = Path.GetPathRoot(RestoreImagePath);
        if (imageRoot is not null
            && string.Equals(imageRoot.TrimEnd('\\'), systemRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            var proceed = await _dialog.ConfirmAsync(
                "WIM이 C:에 있습니다",
                "복원 이미지가 시스템 드라이브에 있으면 복원 중 접근이 끊길 수 있습니다.\n"
                + "USB로 복사한 뒤 그 파일을 선택하세요.\n\n그래도 진행할까요?",
                "진행");
            if (!proceed) return;
        }

        var confirmed = await _dialog.ConfirmAsync(
            "C: 자동 복원",
            "「C: 자동 복원」을 진행하면 컴퓨터가 다시 시작된 뒤, 선택한 백업으로 C: 복원이 자동 시작됩니다.\n\n"
            + $"WIM: {RestoreImagePath}\n\n"
            + "· 백업이 있는 디스크는 연결해 두세요.\n"
            + "· C:가 백업 시점 내용으로 바뀝니다.\n"
            + "· 관리자 권한(UAC)이 필요합니다.\n"
            + "· 복원 중에는 전원을 끄지 마세요.\n\n"
            + "계속할까요?",
            "자동 복원 시작");

        if (!confirmed) return;

        await RunOperationAsync(async (progress, ct) =>
        {
            AppendLog("WinRE 자동 복원 준비 중...");
            var result = await _images.PrepareAutomaticRestoreAsync(RestoreImagePath, progress, ct);
            if (!result.Success)
                throw new InvalidOperationException(result.Error ?? "자동 복원 준비에 실패했습니다.");

            AppendLog("준비 완료. 다시 시작하면 자동 복원이 시작됩니다.");
            StatusMessage = "곧 다시 시작합니다… USB를 뽑지 마세요.";

            var reboot = await _dialog.ConfirmAsync(
                "다시 시작",
                "준비가 끝났습니다.\n\n"
                + "지금 다시 시작하면, 부팅 직후 C: 복원이 자동으로 시작됩니다.\n"
                + "백업 디스크는 연결된 채로 두세요.\n\n"
                + "다시 시작할까요?",
                "다시 시작");

            if (!reboot)
            {
                StatusMessage = "준비만 완료됨. 나중에 고급 시작으로 다시 시작하면 자동 복원이 실행됩니다.";
                AppendLog(StatusMessage);
                return;
            }

            await _images.RebootToWinREAsync(ct);
        });
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        StatusMessage = "취소 요청…";
    }

    private async Task RunOperationAsync(Func<IProgress<SystemImageProgressLine>, CancellationToken, Task> work)
    {
        IsBusy = true;
        ProgressPercent = 0;
        IsProgressIndeterminate = true;
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(ShowProgressPanel));
        OnPropertyChanged(nameof(HasLogLines));
        LogLines.Clear();
        OnPropertyChanged(nameof(HasLogLines));
        OnPropertyChanged(nameof(ShowProgressPanel));

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
            StatusMessage = "관리자 권한이 거부되어 작업을 취소했습니다.";
            AppendLog(StatusMessage);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "작업이 취소되었습니다.";
            AppendLog(StatusMessage);
        }
        catch (Exception ex)
        {
            StatusMessage = "실패: " + ex.Message;
            AppendLog(StatusMessage);
            await _dialog.ShowMessageAsync("시스템 백업 오류", ex.Message);
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
            _cts.Dispose();
            _cts = null;
            OnPropertyChanged(nameof(CanCancel));
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(ShowProgressPanel));
            OnPropertyChanged(nameof(HasLogLines));
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
}
