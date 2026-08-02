using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCustoms.Models;
using WinCustoms.Services;
using WinCustoms.Services.Catalog;

namespace WinCustoms.ViewModels;

/// <summary>
/// 카테고리 페이지의 공통 동작.
///
/// UI 흐름은 "토글로 원하는 상태를 고르고 → 하단에서 한 번에 적용"이다.
/// 토글을 만질 때마다 즉시 적용하지 않기 때문에
///  · UAC 창이 여러 번 뜨지 않고
///  · 실수로 건드린 항목을 적용 전에 되돌릴 수 있다.
/// </summary>
public abstract partial class TweakPageViewModelBase : ObservableObject
{
    protected readonly ITweakEngine Engine;
    protected readonly IShellService Shell;
    protected readonly IDialogService Dialog;

    private readonly ITweakCatalog _catalog;
    private bool _initialized;

    protected TweakPageViewModelBase(
        TweakCategory category,
        string title,
        string subtitle,
        ITweakCatalog catalog,
        ITweakEngine engine,
        IShellService shell,
        IDialogService dialog)
    {
        Category = category;
        Title = title;
        Subtitle = subtitle;
        _catalog = catalog;
        Engine = engine;
        Shell = shell;
        Dialog = dialog;
    }

    public TweakCategory Category { get; }

    public string Title { get; }

    public string Subtitle { get; }

    public ObservableCollection<TweakItem> Tweaks { get; } = [];

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    [ObservableProperty]
    public partial bool HasPendingChanges { get; set; }

    [ObservableProperty]
    public partial int AppliedCount { get; set; }

    /// <summary>페이지에 처음 들어올 때 한 번 목록을 구성하고, 이후에는 상태만 새로 고친다.</summary>
    public void EnsureLoaded()
    {
        if (!_initialized)
        {
            foreach (var tweak in _catalog.GetTweaks(Category))
            {
                tweak.PropertyChanged += OnTweakPropertyChanged;
                Tweaks.Add(tweak);
            }

            _initialized = true;
        }

        Refresh();
    }

    private void OnTweakPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TweakItem.IsDirty) or nameof(TweakItem.IsRequested) or nameof(TweakItem.IsApplied))
            UpdateAggregates();
    }

    private void UpdateAggregates()
    {
        HasPendingChanges = Tweaks.Any(t => t.IsDirty);
        AppliedCount = Tweaks.Count(t => t.IsToggle && t.IsApplied);
    }

    [RelayCommand]
    private void Refresh()
    {
        Engine.RefreshStates(Tweaks);
        UpdateAggregates();
        StatusMessage = null;
    }

    /// <summary>이 페이지의 토글을 모두 켠다. 실제 반영은 하단 '선택 항목 적용' 에서 한다.</summary>
    [RelayCommand]
    private void EnableAll()
    {
        SetAllRequested(true);
        StatusMessage = "모든 항목을 켜 두었습니다. 하단에서 '선택 항목 적용'을 누르세요.";
    }

    /// <summary>이 페이지의 토글을 모두 끈다. 실제 반영은 하단 '선택 항목 적용' 에서 한다.</summary>
    [RelayCommand]
    private void DisableAll()
    {
        SetAllRequested(false);
        StatusMessage = "모든 항목을 꺼 두었습니다. 하단에서 '선택 항목 적용'을 누르세요.";
    }

    private void SetAllRequested(bool requested)
    {
        foreach (var tweak in Tweaks)
        {
            if (tweak.IsToggle)
                tweak.IsRequested = requested;
        }

        UpdateAggregates();
    }

    [RelayCommand]
    private async Task ApplySelectedAsync(CancellationToken ct)
    {
        if (IsBusy) return;

        var pending = Tweaks.Where(t => t.IsDirty).ToList();
        if (pending.Count == 0)
        {
            StatusMessage = "적용할 변경 사항이 없습니다.";
            return;
        }

        var risky = pending.Where(t => t.Risk == TweakRisk.High).ToList();
        if (risky.Count > 0)
        {
            var names = string.Join(", ", risky.Select(t => t.Title));
            var proceed = await Dialog.ConfirmAsync(
                "되돌리기 어려운 항목이 있습니다",
                $"{names}\n\n이 항목은 복원이 완전하지 않을 수 있습니다. 계속할까요?",
                "적용");

            if (!proceed) return;
        }

        await RunAsync(() => Engine.ApplyPendingAsync(Tweaks.ToList(), ct), ct);
    }

    [RelayCommand]
    private async Task RestoreDefaultsAsync(CancellationToken ct)
    {
        if (IsBusy) return;

        var applied = Tweaks.Where(t => t.IsToggle && t.IsApplied).ToList();
        if (applied.Count == 0)
        {
            StatusMessage = "복원할 항목이 없습니다.";
            return;
        }

        var confirmed = await Dialog.ConfirmAsync(
            "기본값으로 복원",
            $"이 페이지에서 적용된 {applied.Count}개 항목을 Windows 기본 상태로 되돌립니다. 계속할까요?",
            "복원");

        if (!confirmed) return;

        await RunAsync(() => Engine.RestoreAllAsync(Tweaks.ToList(), ct), ct);
    }

    [RelayCommand]
    private async Task RunActionAsync(TweakItem? item)
    {
        if (item is null || IsBusy) return;

        if (item.Risk >= TweakRisk.Moderate)
        {
            var confirmed = await Dialog.ConfirmAsync(item.Title, item.Description, item.ActionText);
            if (!confirmed) return;
        }

        await RunAsync(() => Engine.RunActionAsync(item, CancellationToken.None), CancellationToken.None);
    }

    [RelayCommand]
    private async Task OpenLinkAsync(TweakItem? item)
    {
        if (item?.LearnMoreUrl is { Length: > 0 } url)
            await Shell.OpenUrlAsync(url);
    }

    private async Task RunAsync(Func<Task<TweakRunResult>> operation, CancellationToken ct)
    {
        IsBusy = true;
        StatusMessage = null;

        try
        {
            var result = await operation();
            UpdateAggregates();
            await ReportAsync(result, ct);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "작업이 취소되었습니다.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"오류: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReportAsync(TweakRunResult result, CancellationToken ct)
    {
        if (result.Cancelled)
        {
            StatusMessage = "관리자 권한 승인이 취소되어 일부 항목이 적용되지 않았습니다.";
            return;
        }

        if (result.DidNothing)
        {
            StatusMessage = "변경된 항목이 없습니다.";
            return;
        }

        var parts = new List<string> { $"{result.Succeeded}개 항목 적용 완료" };
        if (result.Failed > 0) parts.Add($"{result.Failed}개 실패");

        StatusMessage = string.Join(" · ", parts);

        if (result.Errors.Count > 0)
            await Dialog.ShowMessageAsync("일부 항목을 적용하지 못했습니다", string.Join("\n\n", result.Errors));

        if (result.NeedsExplorerRestart)
        {
            var restart = await Dialog.ConfirmAsync(
                "탐색기를 다시 시작할까요?",
                "변경 내용을 반영하려면 탐색기(explorer.exe)를 재시작해야 합니다. "
                + "열려 있는 폴더 창이 모두 닫히지만 실행 중인 다른 프로그램에는 영향이 없습니다.",
                "지금 다시 시작",
                "나중에");

            if (restart)
            {
                IsBusy = true;
                try
                {
                    await Shell.RestartExplorerAsync(ct);
                    StatusMessage = "탐색기를 다시 시작했습니다.";
                }
                finally
                {
                    IsBusy = false;
                }
            }
        }

        if (result.NeedsSignOut)
        {
            StatusMessage += " (일부 항목은 로그아웃 후 적용됩니다)";
        }
    }
}
