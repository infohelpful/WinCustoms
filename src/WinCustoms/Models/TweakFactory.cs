using WinCustoms.Common;
using WinCustoms.Services;

namespace WinCustoms.Models;

/// <summary>
/// 레지스트리 기반 트윅을 선언적으로 만들어 준다.
/// 스펙 하나만 적으면 적용/복원/상태 감지가 자동으로 쌍을 이루기 때문에
/// "복원 로직을 빠뜨리는" 실수를 구조적으로 막을 수 있다.
/// </summary>
public sealed class TweakFactory(IRegistryService registry)
{
    private readonly IRegistryService _registry = registry;

    public IRegistryService Registry => _registry;

    public TweakItem FromRegistry(
        string id,
        string title,
        string description,
        TweakCategory category,
        IReadOnlyList<RegistryValueSpec> specs,
        IReadOnlyList<(RegistryRoot Root, string SubKey)>? createKeysOnApply = null,
        IReadOnlyList<(RegistryRoot Root, string SubKey)>? deleteKeysOnRestore = null,
        bool requiresExplorerRestart = false,
        bool requiresSignOut = false,
        TweakRisk risk = TweakRisk.Safe,
        string? learnMoreUrl = null,
        Func<bool>? detect = null)
    {
        var createKeys = createKeysOnApply ?? [];
        var deleteKeys = deleteKeysOnRestore ?? [];

        var applyOps = new List<RegistryOperation>(createKeys.Count + specs.Count);
        applyOps.AddRange(createKeys.Select(k => RegistryOperation.CreateKey(k.Root, k.SubKey)));
        applyOps.AddRange(specs.Select(s => s.ToApplyOperation()));

        var restoreOps = new List<RegistryOperation>(specs.Count + deleteKeys.Count);
        // 값을 먼저 되돌린 다음 키를 통째로 지운다(순서가 반대면 값 복원이 무의미해진다).
        restoreOps.AddRange(specs.Where(s => !IsInsideAnyKey(s, deleteKeys)).Select(s => s.ToRestoreOperation()));
        restoreOps.AddRange(deleteKeys.Select(k => RegistryOperation.DeleteKeyTree(k.Root, k.SubKey)));

        var requiresAdmin = applyOps.Concat(restoreOps).Any(o => o.RequiresElevation);

        var backupKeys = specs
            .Select(s => (s.Root, s.SubKey))
            .Concat(createKeys)
            .Concat(deleteKeys)
            .Distinct()
            .ToList();

        return new TweakItem
        {
            Id = id,
            Title = title,
            Description = description,
            Category = category,
            Kind = TweakKind.Toggle,
            Risk = risk,
            RequiresAdmin = requiresAdmin,
            RequiresExplorerRestart = requiresExplorerRestart,
            RequiresSignOut = requiresSignOut,
            LearnMoreUrl = learnMoreUrl,
            BackupKeys = backupKeys,
            OfflineApplyOperations = applyOps,
            DetectApplied = detect ?? (() => _registry.IsApplied(specs)),
            ApplyAction = ct => _registry.ExecuteAsync(applyOps, ct),
            RestoreAction = ct => _registry.ExecuteAsync(restoreOps, ct)
        };
    }

    /// <summary>임의의 코드를 실행하는 토글 트윅(레지스트리로 표현되지 않는 경우).</summary>
    public TweakItem Custom(
        string id,
        string title,
        string description,
        TweakCategory category,
        Func<CancellationToken, Task> apply,
        Func<CancellationToken, Task> restore,
        Func<bool> detect,
        bool requiresAdmin = false,
        bool requiresExplorerRestart = false,
        bool requiresSignOut = false,
        TweakRisk risk = TweakRisk.Safe,
        string? learnMoreUrl = null) => new()
        {
            Id = id,
            Title = title,
            Description = description,
            Category = category,
            Kind = TweakKind.Toggle,
            Risk = risk,
            RequiresAdmin = requiresAdmin,
            RequiresExplorerRestart = requiresExplorerRestart,
            RequiresSignOut = requiresSignOut,
            LearnMoreUrl = learnMoreUrl,
            ApplyAction = apply,
            RestoreAction = restore,
            DetectApplied = detect
        };

    /// <summary>버튼 한 번으로 끝나는 동작형 트윅.</summary>
    public TweakItem Action(
        string id,
        string title,
        string description,
        TweakCategory category,
        string actionText,
        Func<CancellationToken, Task> run,
        Func<CancellationToken, Task>? undo = null,
        bool requiresAdmin = false,
        TweakRisk risk = TweakRisk.Moderate,
        string? learnMoreUrl = null) => new()
        {
            Id = id,
            Title = title,
            Description = description,
            Category = category,
            Kind = TweakKind.Action,
            Risk = risk,
            RequiresAdmin = requiresAdmin,
            ActionText = actionText,
            LearnMoreUrl = learnMoreUrl,
            ApplyAction = run,
            RestoreAction = undo ?? (_ => Task.CompletedTask)
        };

    private static bool IsInsideAnyKey(RegistryValueSpec spec, IReadOnlyList<(RegistryRoot Root, string SubKey)> keys)
        => keys.Any(k => k.Root == spec.Root
                         && spec.SubKey.StartsWith(k.SubKey, StringComparison.OrdinalIgnoreCase));
}
