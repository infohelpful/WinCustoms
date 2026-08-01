using System.Text.Json;
using Microsoft.Win32;
using WinCustoms.Common;

namespace WinCustoms.Services;

public interface IContextMenuService
{
    /// <summary>사용자가 등록한 우클릭 항목 목록.</summary>
    Task<IReadOnlyList<CustomContextMenuEntry>> LoadAsync(CancellationToken ct = default);

    Task AddAsync(CustomContextMenuEntry entry, CancellationToken ct = default);

    Task RemoveAsync(CustomContextMenuEntry entry, CancellationToken ct = default);

    /// <summary>표시 이름으로부터 레지스트리 키에 안전한 식별자를 만든다.</summary>
    string CreateKeyName(string displayName);
}

/// <summary>
/// 사용자가 지정한 프로그램을 HKCU\Software\Classes 아래 우클릭 메뉴로 등록한다.
/// 관리자 권한이 필요 없고, WinCustoms.User. 접두사로 자기 항목만 안전하게 식별한다.
/// </summary>
public sealed class ContextMenuService(IRegistryService registry) : IContextMenuService
{
    private const string UserEntryPrefix = RegistryPaths.ContextEntryPrefix + "User.";

    private readonly IRegistryService _registry = registry;

    private static string ManifestPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinCustoms", "context-menu.json");

    public string CreateKeyName(string displayName)
    {
        var cleaned = new string(displayName
            .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_')
            .ToArray());

        if (cleaned.Length == 0)
            cleaned = "Entry";

        return cleaned.Length > 40 ? cleaned[..40] : cleaned;
    }

    public async Task<IReadOnlyList<CustomContextMenuEntry>> LoadAsync(CancellationToken ct = default)
    {
        var entries = await ReadManifestAsync(ct).ConfigureAwait(false);

        // 매니페스트에는 있지만 레지스트리에서 사라진 항목은 정리한다(수동 편집 대비).
        var alive = entries
            .Where(e => _registry.KeyExists(RegistryRoot.CurrentUser, CommandKey(e, Scope.Files))
                        || _registry.KeyExists(RegistryRoot.CurrentUser, CommandKey(e, Scope.Folders))
                        || _registry.KeyExists(RegistryRoot.CurrentUser, CommandKey(e, Scope.Background)))
            .ToList();

        if (alive.Count != entries.Count)
            await WriteManifestAsync(alive, ct).ConfigureAwait(false);

        return alive;
    }

    public async Task AddAsync(CustomContextMenuEntry entry, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(entry.DisplayName))
            throw new ArgumentException("표시할 이름을 입력하세요.", nameof(entry));

        if (!File.Exists(entry.ExecutablePath))
            throw new FileNotFoundException("선택한 프로그램을 찾을 수 없습니다.", entry.ExecutablePath);

        if (string.IsNullOrEmpty(entry.Key))
            entry.Key = CreateKeyName(entry.DisplayName);

        var ops = new List<RegistryOperation>();

        if (entry.ShowForFiles)
            ops.AddRange(BuildScopeOperations(entry, Scope.Files));

        if (entry.ShowForFolders)
        {
            ops.AddRange(BuildScopeOperations(entry, Scope.Folders));
            ops.AddRange(BuildScopeOperations(entry, Scope.Background));
        }

        if (ops.Count == 0)
            throw new InvalidOperationException("파일 또는 폴더 중 최소 하나는 선택해야 합니다.");

        await _registry.ExecuteAsync(ops, ct).ConfigureAwait(false);

        var entries = (await ReadManifestAsync(ct).ConfigureAwait(false))
            .Where(e => !string.Equals(e.Key, entry.Key, StringComparison.OrdinalIgnoreCase))
            .ToList();

        entries.Add(entry);
        await WriteManifestAsync(entries, ct).ConfigureAwait(false);

        NativeMethods.NotifyShellAssociationChanged();
    }

    public async Task RemoveAsync(CustomContextMenuEntry entry, CancellationToken ct = default)
    {
        var ops = new List<RegistryOperation>
        {
            RegistryOperation.DeleteKeyTree(RegistryRoot.CurrentUser, ShellKey(entry, Scope.Files)),
            RegistryOperation.DeleteKeyTree(RegistryRoot.CurrentUser, ShellKey(entry, Scope.Folders)),
            RegistryOperation.DeleteKeyTree(RegistryRoot.CurrentUser, ShellKey(entry, Scope.Background))
        };

        await _registry.ExecuteAsync(ops, ct).ConfigureAwait(false);

        var entries = (await ReadManifestAsync(ct).ConfigureAwait(false))
            .Where(e => !string.Equals(e.Key, entry.Key, StringComparison.OrdinalIgnoreCase))
            .ToList();

        await WriteManifestAsync(entries, ct).ConfigureAwait(false);

        NativeMethods.NotifyShellAssociationChanged();
    }

    // ── 내부 구현 ────────────────────────────────────────────────

    private enum Scope
    {
        Files,
        Folders,
        Background
    }

    private static string ScopeRoot(Scope scope) => scope switch
    {
        Scope.Files => RegistryPaths.AllFilesShell,
        Scope.Folders => RegistryPaths.DirectoryShell,
        Scope.Background => RegistryPaths.DirectoryBackgroundShell,
        _ => throw new ArgumentOutOfRangeException(nameof(scope))
    };

    private static string ShellKey(CustomContextMenuEntry entry, Scope scope)
        => $@"{ScopeRoot(scope)}\{UserEntryPrefix}{entry.Key}";

    private static string CommandKey(CustomContextMenuEntry entry, Scope scope)
        => $@"{ShellKey(entry, scope)}\command";

    private static IEnumerable<RegistryOperation> BuildScopeOperations(CustomContextMenuEntry entry, Scope scope)
    {
        var shellKey = ShellKey(entry, scope);
        var commandKey = CommandKey(entry, scope);

        // %1 = 선택한 파일/폴더, %V = 폴더 배경에서 우클릭한 현재 폴더
        var argumentToken = scope == Scope.Background ? "%V" : "%1";
        var command = entry.PassTargetPath
            ? $@"""{entry.ExecutablePath}"" ""{argumentToken}"""
            : $@"""{entry.ExecutablePath}""";

        yield return RegistryOperation.Set(
            RegistryRoot.CurrentUser, shellKey, string.Empty, RegistryValueKind.String, entry.DisplayName);

        yield return RegistryOperation.Set(
            RegistryRoot.CurrentUser, shellKey, "Icon", RegistryValueKind.String, entry.ExecutablePath);

        yield return RegistryOperation.Set(
            RegistryRoot.CurrentUser, commandKey, string.Empty, RegistryValueKind.String, command);
    }

    private static async Task<List<CustomContextMenuEntry>> ReadManifestAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(ManifestPath)) return [];

            var json = await File.ReadAllTextAsync(ManifestPath, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize(json, WinCustomsJsonContext.Default.ListCustomContextMenuEntry) ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
    }

    private static async Task WriteManifestAsync(List<CustomContextMenuEntry> entries, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(ManifestPath)!;
        Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(entries, WinCustomsJsonContext.Default.ListCustomContextMenuEntry);
        await File.WriteAllTextAsync(ManifestPath, json, ct).ConfigureAwait(false);
    }
}
