using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCustoms.Common;
using WinCustoms.Services;

namespace WinCustoms.ViewModels;

/// <summary>
/// 우클릭 메뉴를 다루는 두 탭을 함께 담당한다.
/// 제거 탭은 시스템에 이미 올라와 있는 항목을 숨기고 되살리며,
/// 등록 탭은 사용자가 고른 프로그램을 새로 추가한다.
/// </summary>
public sealed partial class ContextMenuEditorViewModel : ObservableObject
{
    public const int RemoveTab = 0;
    public const int RegisterTab = 1;

    private readonly IContextMenuService _contextMenu;
    private readonly IShellMenuInventoryService _inventory;
    private readonly IDialogService _dialog;
    private readonly IShellService _shell;

    /// <summary>필터를 거치기 전 스캔 결과 전체.</summary>
    private readonly List<ShellMenuEntry> _scanned = [];

    public ContextMenuEditorViewModel(
        IContextMenuService contextMenu,
        IShellMenuInventoryService inventory,
        IDialogService dialog,
        IShellService shell)
    {
        _contextMenu = contextMenu;
        _inventory = inventory;
        _dialog = dialog;
        _shell = shell;

        NewDisplayName = string.Empty;
        NewExecutablePath = string.Empty;
        SearchText = string.Empty;
        PassTargetPath = true;
        ShowForFiles = true;
        ShowForFolders = true;
    }

    // ── 탭 ────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRemoveTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsRegisterTabSelected))]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    public partial int SelectedTabIndex { get; set; }

    public bool IsRemoveTabSelected => SelectedTabIndex == RemoveTab;

    public bool IsRegisterTabSelected => SelectedTabIndex == RegisterTab;

    public string Subtitle => IsRegisterTabSelected
        ? "자주 쓰는 프로그램을 마우스 오른쪽 버튼 메뉴에 추가합니다. HKCU 범위에만 기록하므로 관리자 권한이 필요 없습니다."
        : "지금 우클릭 메뉴에 올라와 있는 항목입니다. 토글을 끄면 메뉴에서만 사라지고, 다시 켜면 그대로 돌아옵니다.";

    // ── 제거 탭 ───────────────────────────────────────────────────

    public ObservableCollection<ShellMenuEntry> InstalledEntries { get; } = [];

    [ObservableProperty]
    public partial bool ShowSystemEntries { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; }

    [ObservableProperty]
    public partial bool IsScanning { get; set; }

    public bool HasInstalledEntries => InstalledEntries.Count > 0;

    public string InventorySummary
    {
        get
        {
            if (_scanned.Count == 0) return string.Empty;

            var hidden = _scanned.Count(e => !e.IsEnabled);
            return $"{InstalledEntries.Count}개 표시 중 · 숨긴 항목 {hidden}개";
        }
    }

    public string EmptyInventoryMessage => ShowSystemEntries || _scanned.Count == 0
        ? "표시할 항목이 없습니다."
        : "설치한 프로그램이 추가한 항목이 없습니다. 'Windows 기본 항목도 표시'를 켜면 시스템 항목까지 볼 수 있습니다.";

    [RelayCommand]
    private async Task ScanAsync(CancellationToken ct)
    {
        IsScanning = true;
        try
        {
            var entries = await _inventory.ScanAsync(ct);

            _scanned.Clear();
            _scanned.AddRange(entries);

            ApplyInventoryFilter();
        }
        catch (Exception ex)
        {
            StatusMessage = $"우클릭 메뉴를 읽지 못했습니다: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private async Task ToggleEntryAsync(ShellMenuEntry? entry)
    {
        // ToggleSwitch 는 바인딩으로 값이 채워질 때도 Toggled 를 올린다.
        // 실제로 반영된 상태와 다를 때만 레지스트리를 건드린다.
        if (entry is null || entry.IsBusy || entry.IsEnabled == entry.AppliedEnabled) return;

        var target = entry.IsEnabled;

        entry.IsBusy = true;
        try
        {
            await _inventory.SetEnabledAsync(entry, target, CancellationToken.None);
            entry.AppliedEnabled = target;

            var restart = entry.NeedsExplorerRestart ? " 탐색기를 다시 시작해야 반영됩니다." : string.Empty;
            StatusMessage = target
                ? $"'{entry.DisplayName}' 항목을 다시 표시합니다.{restart}"
                : $"'{entry.DisplayName}' 항목을 메뉴에서 숨겼습니다.{restart}";

            OnPropertyChanged(nameof(InventorySummary));
        }
        catch (Exception ex)
        {
            entry.IsEnabled = entry.AppliedEnabled;
            StatusMessage = $"'{entry.DisplayName}' 변경 실패: {ex.Message}";
        }
        finally
        {
            entry.IsBusy = false;
        }
    }

    partial void OnShowSystemEntriesChanged(bool value) => ApplyInventoryFilter();

    partial void OnSearchTextChanged(string value) => ApplyInventoryFilter();

    private void ApplyInventoryFilter()
    {
        var term = SearchText?.Trim() ?? string.Empty;

        InstalledEntries.Clear();

        foreach (var entry in _scanned)
        {
            if (entry.IsSystem && !ShowSystemEntries) continue;
            if (term.Length > 0 && !Matches(entry, term)) continue;

            InstalledEntries.Add(entry);
        }

        OnPropertyChanged(nameof(HasInstalledEntries));
        OnPropertyChanged(nameof(InventorySummary));
        OnPropertyChanged(nameof(EmptyInventoryMessage));
    }

    private static bool Matches(ShellMenuEntry entry, string term)
        => entry.DisplayName.Contains(term, StringComparison.CurrentCultureIgnoreCase)
           || entry.Detail.Contains(term, StringComparison.OrdinalIgnoreCase);

    // ── 등록 탭 ───────────────────────────────────────────────────

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

        await ScanAsync(ct);
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

        await ScanAsync(CancellationToken.None);
    }

    [RelayCommand]
    private Task RestartExplorerAsync(CancellationToken ct) => _shell.RestartExplorerAsync(ct);
}
