using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCustoms.Common;
using WinCustoms.Services;

namespace WinCustoms.ViewModels;

/// <summary>자주 쓰는 프로그램을 우클릭 메뉴에 등록/제거한다.</summary>
public sealed partial class ContextMenuEditorViewModel : ObservableObject
{
    private readonly IContextMenuService _contextMenu;
    private readonly IDialogService _dialog;
    private readonly IShellService _shell;

    public ContextMenuEditorViewModel(
        IContextMenuService contextMenu,
        IDialogService dialog,
        IShellService shell)
    {
        _contextMenu = contextMenu;
        _dialog = dialog;
        _shell = shell;

        NewDisplayName = string.Empty;
        NewExecutablePath = string.Empty;
        PassTargetPath = true;
        ShowForFiles = true;
        ShowForFolders = true;
    }

    public ObservableCollection<CustomContextMenuEntry> Entries { get; } = [];

    [ObservableProperty]
    public partial string NewDisplayName { get; set; }

    [ObservableProperty]
    public partial string NewExecutablePath { get; set; }

    [ObservableProperty]
    public partial bool PassTargetPath { get; set; }

    [ObservableProperty]
    public partial bool ShowForFiles { get; set; }

    [ObservableProperty]
    public partial bool ShowForFolders { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    public bool HasEntries => Entries.Count > 0;

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            var entries = await _contextMenu.LoadAsync(ct);

            Entries.Clear();
            foreach (var entry in entries)
                Entries.Add(entry);

            OnPropertyChanged(nameof(HasEntries));
        }
        catch (Exception ex)
        {
            StatusMessage = $"목록을 읽지 못했습니다: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var path = await _dialog.PickExecutableAsync();
        if (string.IsNullOrEmpty(path)) return;

        NewExecutablePath = path;

        if (string.IsNullOrWhiteSpace(NewDisplayName))
            NewDisplayName = Path.GetFileNameWithoutExtension(path);
    }

    [RelayCommand]
    private async Task AddAsync(CancellationToken ct)
    {
        if (IsBusy) return;

        if (string.IsNullOrWhiteSpace(NewDisplayName) || string.IsNullOrWhiteSpace(NewExecutablePath))
        {
            StatusMessage = "프로그램과 표시할 이름을 모두 입력하세요.";
            return;
        }

        var entry = new CustomContextMenuEntry
        {
            Key = _contextMenu.CreateKeyName(NewDisplayName),
            DisplayName = NewDisplayName.Trim(),
            ExecutablePath = NewExecutablePath.Trim(),
            PassTargetPath = PassTargetPath,
            ShowForFiles = ShowForFiles,
            ShowForFolders = ShowForFolders
        };

        IsBusy = true;
        try
        {
            await _contextMenu.AddAsync(entry, ct);
            await LoadAsync(ct);

            NewDisplayName = string.Empty;
            NewExecutablePath = string.Empty;
            StatusMessage = $"'{entry.DisplayName}' 항목을 추가했습니다. 클래식 우클릭 메뉴가 꺼져 있으면 '추가 옵션 표시' 안에 나타납니다.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"추가 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RemoveAsync(CustomContextMenuEntry? entry)
    {
        if (entry is null || IsBusy) return;

        var confirmed = await _dialog.ConfirmAsync(
            "우클릭 항목 삭제",
            $"'{entry.DisplayName}' 항목을 우클릭 메뉴에서 제거합니다. 프로그램 자체는 삭제되지 않습니다.",
            "삭제");

        if (!confirmed) return;

        IsBusy = true;
        try
        {
            await _contextMenu.RemoveAsync(entry, CancellationToken.None);
            Entries.Remove(entry);
            OnPropertyChanged(nameof(HasEntries));
            StatusMessage = $"'{entry.DisplayName}' 항목을 제거했습니다.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"삭제 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task RestartExplorerAsync(CancellationToken ct) => _shell.RestartExplorerAsync(ct);
}
