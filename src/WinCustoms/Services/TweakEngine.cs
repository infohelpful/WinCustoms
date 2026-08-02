using WinCustoms.Common;
using WinCustoms.Models;

namespace WinCustoms.Services;

public sealed record TweakRunResult
{
    public int Succeeded { get; init; }
    public int Failed { get; init; }
    public bool NeedsExplorerRestart { get; init; }
    public bool NeedsSignOut { get; init; }
    public bool Cancelled { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];

    public bool DidNothing => Succeeded == 0 && Failed == 0 && !Cancelled;

    public static readonly TweakRunResult Empty = new();
}

public interface ITweakEngine
{
    void RefreshStates(IEnumerable<TweakItem> items);

    /// <summary>토글 위치가 실제 상태와 다른 항목만 적용/복원한다.</summary>
    Task<TweakRunResult> ApplyPendingAsync(IReadOnlyList<TweakItem> items, CancellationToken ct = default);

    /// <summary>지정한 항목을 모두 기본 상태로 되돌린다.</summary>
    Task<TweakRunResult> RestoreAllAsync(IReadOnlyList<TweakItem> items, CancellationToken ct = default);

    /// <summary>동작형 트윅 하나를 실행한다.</summary>
    Task<TweakRunResult> RunActionAsync(TweakItem item, CancellationToken ct = default);
}

/// <summary>
/// 트윅 실행을 한곳에 모은다. ViewModel 은 "무엇을" 할지만 정하고,
/// 백업 · 오류 수집 · 재시작 필요 여부 판단은 모두 엔진이 담당한다.
/// </summary>
public sealed class TweakEngine(IRegistryService registry, IShellService shell) : ITweakEngine
{
    private readonly IRegistryService _registry = registry;
    private readonly IShellService _shell = shell;

    public void RefreshStates(IEnumerable<TweakItem> items)
    {
        foreach (var item in items)
            item.RefreshState();
    }

    public Task<TweakRunResult> ApplyPendingAsync(IReadOnlyList<TweakItem> items, CancellationToken ct = default)
    {
        var targets = items
            .Where(i => i.Kind == TweakKind.Toggle && i.IsDirty)
            .Select(i => (Item: i, Apply: i.IsRequested))
            .ToList();

        return RunBatchAsync(targets, "apply", ct);
    }

    public Task<TweakRunResult> RestoreAllAsync(IReadOnlyList<TweakItem> items, CancellationToken ct = default)
    {
        var targets = items
            .Where(i => i.Kind == TweakKind.Toggle && i.IsApplied)
            .Select(i => (Item: i, Apply: false))
            .ToList();

        return RunBatchAsync(targets, "restore", ct);
    }

    public async Task<TweakRunResult> RunActionAsync(TweakItem item, CancellationToken ct = default)
    {
        var targets = new List<(TweakItem Item, bool Apply)> { (item, true) };
        return await RunBatchAsync(targets, "action", ct).ConfigureAwait(false);
    }

    private async Task<TweakRunResult> RunBatchAsync(
        List<(TweakItem Item, bool Apply)> targets,
        string label,
        CancellationToken ct)
    {
        if (targets.Count == 0) return TweakRunResult.Empty;

        // 변경 전 상태를 .reg 로 남긴다. 실패해도 진행은 막지 않는다.
        var backupKeys = targets.SelectMany(t => t.Item.BackupKeys).Distinct().ToList();
        if (backupKeys.Count > 0)
        {
            try
            {
                await _registry.ExportBackupAsync(backupKeys, label, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 백업은 최선 노력(best-effort)이다.
            }
        }

        var succeeded = 0;
        var errors = new List<string>();
        var needsExplorerRestart = false;
        var needsSignOut = false;
        var cancelled = false;

        foreach (var (item, apply) in targets)
        {
            ct.ThrowIfCancellationRequested();

            await UiThread.InvokeAsync(() =>
            {
                item.IsBusy = true;
                item.LastError = null;
            }).ConfigureAwait(false);

            try
            {
                if (apply)
                    await item.ApplyAction(ct).ConfigureAwait(false);
                else
                    await item.RestoreAction(ct).ConfigureAwait(false);

                succeeded++;
                needsExplorerRestart |= item.RequiresExplorerRestart;
                needsSignOut |= item.RequiresSignOut;
            }
            catch (ElevationDeniedException)
            {
                // UAC 를 거부하면 이후 항목도 어차피 실패하므로 배치를 중단한다.
                cancelled = true;
                await UiThread.InvokeAsync(() =>
                {
                    item.LastError = "관리자 권한 승인이 취소되었습니다.";
                }).ConfigureAwait(false);
                break;
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                break;
            }
            catch (Exception ex)
            {
                await UiThread.InvokeAsync(() =>
                {
                    item.LastError = ex.Message;
                }).ConfigureAwait(false);
                errors.Add($"[{item.Title}] {ex.Message}");
            }
            finally
            {
                await UiThread.InvokeAsync(() =>
                {
                    item.IsBusy = false;
                    item.RefreshState();
                }).ConfigureAwait(false);
            }
        }

        // 탐색기를 재시작하지 않아도 되는 변경은 셸 통지만으로 반영되는 경우가 있다.
        _shell.NotifyShellSettingsChanged();

        return new TweakRunResult
        {
            Succeeded = succeeded,
            Failed = errors.Count,
            NeedsExplorerRestart = needsExplorerRestart,
            NeedsSignOut = needsSignOut,
            Cancelled = cancelled,
            Errors = errors
        };
    }
}
