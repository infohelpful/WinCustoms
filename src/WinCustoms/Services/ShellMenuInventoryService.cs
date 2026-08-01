using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;
using WinCustoms.Common;

namespace WinCustoms.Services;

/// <summary>우클릭 항목이 등록되는 셸 클래스. 같은 프로그램이 여러 범위에 동시에 등록하기도 한다.</summary>
public enum ShellMenuScope
{
    AllFiles,
    AllFilesystemObjects,
    Directory,
    DirectoryBackground,
    Folder,
    Drive
}

/// <summary>
/// 항목이 메뉴에 실리는 방식. 숨기는 수단이 서로 달라서 구분해 둔다.
/// <see cref="Verb"/> 는 <c>shell\&lt;동사&gt;</c> 키에 LegacyDisable 을 심고,
/// <see cref="Handler"/> 는 COM 확장이므로 셸 확장 차단 목록에 CLSID 를 넣는다.
/// </summary>
public enum ShellMenuEntryKind
{
    Verb,
    Handler
}

/// <summary>동사 하나가 실제로 존재하는 레지스트리 키. 범위마다 별도 키가 생긴다.</summary>
public sealed record ShellMenuTarget(RegistryRoot Root, string SubKey, ShellMenuScope Scope);

/// <summary>현재 우클릭 메뉴에 올라와 있는 항목 하나.</summary>
public sealed partial class ShellMenuEntry : ObservableObject
{
    public required string Id { get; init; }
    public required ShellMenuEntryKind Kind { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>실행 파일 또는 확장 DLL 경로. 알아낼 수 없으면 CLSID 나 원본 명령줄.</summary>
    public required string Detail { get; init; }

    /// <summary>"파일, 폴더 배경" 처럼 사람이 읽을 수 있는 적용 범위.</summary>
    public required string ScopeText { get; init; }

    /// <summary>Handler 인 경우 차단 목록에 넣을 CLSID.</summary>
    public string? Clsid { get; init; }

    public IReadOnlyList<ShellMenuTarget> Targets { get; init; } = [];

    /// <summary>Windows 자체 항목으로 보인다. 기본 목록에서는 감춰 잡음을 줄인다.</summary>
    public bool IsSystem { get; init; }

    /// <summary>WinCustoms 로 직접 등록한 항목.</summary>
    public bool IsOwn { get; init; }

    /// <summary>끄고 켤 때 UAC 가 필요한지.</summary>
    public bool NeedsElevation { get; init; }

    /// <summary>탐색기를 다시 시작해야 반영되는지. 이미 로드된 COM 확장이 여기 해당한다.</summary>
    public bool NeedsExplorerRestart => Kind == ShellMenuEntryKind.Handler;

    /// <summary>
    /// 레지스트리에 실제로 반영되어 있는 상태.
    /// ToggleSwitch 는 로드/재사용 시점에도 Toggled 를 올리므로,
    /// 이 값과 비교해 사용자가 실제로 움직인 경우만 걸러낸다.
    /// </summary>
    public bool AppliedEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }
}

public interface IShellMenuInventoryService
{
    /// <summary>현재 우클릭 메뉴에 등록되어 있는 항목을 모두 훑어 온다.</summary>
    Task<IReadOnlyList<ShellMenuEntry>> ScanAsync(CancellationToken ct = default);

    /// <summary>항목을 메뉴에서 숨기거나 다시 표시한다. 삭제가 아니라 되돌릴 수 있는 변경이다.</summary>
    Task SetEnabledAsync(ShellMenuEntry entry, bool enabled, CancellationToken ct = default);
}

/// <summary>
/// HKCU · HKLM 의 셸 클래스를 훑어 우클릭 메뉴 항목을 모으고, 숨김/표시를 전환한다.
///
/// 어느 쪽도 키를 지우지 않는다. 동사는 LegacyDisable 값 하나를 붙였다 떼고,
/// COM 확장은 차단 목록에 CLSID 를 넣었다 뺀다. 그래서 되돌리면 원래 상태와 완전히 같아진다.
/// </summary>
public sealed class ShellMenuInventoryService(IRegistryService registry) : IShellMenuInventoryService
{
    private static readonly (RegistryRoot Root, string Classes)[] Hives =
    [
        (RegistryRoot.CurrentUser, RegistryPaths.ClassesRootUser),
        (RegistryRoot.LocalMachine, RegistryPaths.ClassesRootMachine)
    ];

    private static readonly (ShellMenuScope Scope, string ClassName)[] ScopeClasses =
    [
        (ShellMenuScope.AllFiles, "*"),
        (ShellMenuScope.AllFilesystemObjects, "AllFilesystemObjects"),
        (ShellMenuScope.Directory, "Directory"),
        (ShellMenuScope.DirectoryBackground, @"Directory\Background"),
        (ShellMenuScope.Folder, "Folder"),
        (ShellMenuScope.Drive, "Drive")
    ];

    /// <summary>메뉴 문자열이 담기는 값. MUIVerb 가 우선이고 없으면 키의 기본값을 쓴다.</summary>
    private static readonly string[] VerbNameValues = ["MUIVerb", ""];

    private readonly IRegistryService _registry = registry;

    public Task<IReadOnlyList<ShellMenuEntry>> ScanAsync(CancellationToken ct = default)
        => Task.Run(() => Scan(ct), ct);

    public async Task SetEnabledAsync(ShellMenuEntry entry, bool enabled, CancellationToken ct = default)
    {
        var ops = new List<RegistryOperation>();
        var backupKeys = new List<(RegistryRoot Root, string SubKey)>();

        if (entry.Kind == ShellMenuEntryKind.Handler)
        {
            if (string.IsNullOrEmpty(entry.Clsid))
                throw new InvalidOperationException("이 항목의 CLSID 를 알 수 없어 숨길 수 없습니다.");

            backupKeys.Add((RegistryRoot.LocalMachine, RegistryPaths.ShellExtensionsBlocked));

            ops.Add(enabled
                ? RegistryOperation.DeleteValue(
                    RegistryRoot.LocalMachine, RegistryPaths.ShellExtensionsBlocked, entry.Clsid)
                : RegistryOperation.Set(
                    RegistryRoot.LocalMachine, RegistryPaths.ShellExtensionsBlocked, entry.Clsid,
                    RegistryValueKind.String, entry.DisplayName));
        }
        else
        {
            foreach (var target in entry.Targets)
            {
                backupKeys.Add((target.Root, target.SubKey));

                ops.Add(enabled
                    ? RegistryOperation.DeleteValue(target.Root, target.SubKey, RegistryPaths.LegacyDisableValue)
                    : RegistryOperation.Set(target.Root, target.SubKey, RegistryPaths.LegacyDisableValue,
                        RegistryValueKind.String, string.Empty));
            }
        }

        if (ops.Count == 0) return;

        await _registry.ExportBackupAsync(backupKeys, $"contextmenu_{entry.DisplayName}", ct).ConfigureAwait(false);
        await _registry.ExecuteAsync(ops, ct).ConfigureAwait(false);

        NativeMethods.NotifyShellAssociationChanged();
    }

    // ── 스캔 ──────────────────────────────────────────────────────

    private IReadOnlyList<ShellMenuEntry> Scan(CancellationToken ct)
    {
        var blocked = ReadBlockedClsids();
        var builders = new Dictionary<string, EntryBuilder>(StringComparer.OrdinalIgnoreCase);

        foreach (var (root, classes) in Hives)
        {
            foreach (var (scope, className) in ScopeClasses)
            {
                ct.ThrowIfCancellationRequested();

                CollectVerbs(builders, root, $@"{classes}\{className}\shell", scope);
                CollectHandlers(builders, blocked, root,
                    $@"{classes}\{className}\{RegistryPaths.ContextMenuHandlersSuffix}", scope);
            }
        }

        return builders.Values
            .Select(b => b.Build())
            .OrderByDescending(e => e.IsOwn)
            .ThenBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private void CollectVerbs(
        Dictionary<string, EntryBuilder> builders, RegistryRoot root, string shellKey, ShellMenuScope scope)
    {
        foreach (var verb in _registry.GetSubKeyNames(root, shellKey))
        {
            var verbKey = $@"{shellKey}\{verb}";

            // 설계상 메뉴에 나오지 않는 동사다. 숨길 것도 없으므로 목록에 넣지 않는다.
            if (_registry.ReadValue(root, verbKey, RegistryPaths.ProgrammaticAccessOnlyValue) is not null)
                continue;

            var command = _registry.ReadString(root, $@"{verbKey}\command", string.Empty);
            var executable = ExtractExecutable(command);
            var hidden = _registry.ReadValue(root, verbKey, RegistryPaths.LegacyDisableValue) is not null;

            // 같은 동사가 여러 범위·하이브에 흩어져 있어도 사용자에게는 프로그램 하나로 보여야 한다.
            var builder = GetOrAdd(builders, $"verb:{verb.ToLowerInvariant()}", ShellMenuEntryKind.Verb);

            builder.Targets.Add(new ShellMenuTarget(root, verbKey, scope));
            builder.Scopes.Add(scope);
            builder.AnyVisible |= !hidden;
            builder.NeedsElevation |= root == RegistryRoot.LocalMachine;
            builder.IsOwn |= verb.StartsWith(RegistryPaths.ContextEntryPrefix, StringComparison.OrdinalIgnoreCase);

            builder.DisplayName ??= ResolveVerbName(root, verbKey, verb);
            builder.Detail ??= Prefer(executable, command);
            builder.SystemPath ??= executable;
            builder.LooksBuiltIn |= verb.StartsWith("Windows.", StringComparison.OrdinalIgnoreCase);
        }
    }

    private void CollectHandlers(
        Dictionary<string, EntryBuilder> builders,
        HashSet<string> blocked,
        RegistryRoot root,
        string handlersKey,
        ShellMenuScope scope)
    {
        foreach (var handler in _registry.GetSubKeyNames(root, handlersKey))
        {
            var clsid = NormalizeClsid(_registry.ReadString(root, $@"{handlersKey}\{handler}", string.Empty));

            // 키 이름 자체가 CLSID 인 경우도 흔하다.
            if (clsid is null && LooksLikeClsid(handler))
                clsid = NormalizeClsid(handler);

            // CLSID 를 모르면 차단 목록에 넣을 수단이 없어 토글해 줄 수 없다.
            if (clsid is null) continue;

            var builder = GetOrAdd(builders, $"handler:{clsid}", ShellMenuEntryKind.Handler);

            builder.Clsid = clsid;
            builder.Scopes.Add(scope);
            builder.AnyVisible |= !blocked.Contains(clsid);
            builder.NeedsElevation = true; // 차단 목록은 HKLM 에만 있다.

            builder.DisplayName ??= ResolveHandlerName(handler, clsid);
            builder.SystemPath ??= ReadHandlerLibrary(clsid);
            builder.Detail ??= Prefer(builder.SystemPath, clsid);
        }
    }

    private static EntryBuilder GetOrAdd(
        Dictionary<string, EntryBuilder> builders, string id, ShellMenuEntryKind kind)
    {
        if (builders.TryGetValue(id, out var existing)) return existing;

        var created = new EntryBuilder { Id = id, Kind = kind };
        builders[id] = created;
        return created;
    }

    private HashSet<string> ReadBlockedClsids()
    {
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in _registry.GetValueNames(RegistryRoot.LocalMachine, RegistryPaths.ShellExtensionsBlocked))
        {
            if (NormalizeClsid(name) is { } clsid)
                blocked.Add(clsid);
        }

        return blocked;
    }

    // ── 이름 · 경로 해석 ──────────────────────────────────────────

    private string ResolveVerbName(RegistryRoot root, string verbKey, string fallback)
    {
        foreach (var valueName in VerbNameValues)
        {
            if (ResolveDisplayString(_registry.ReadString(root, verbKey, valueName)) is { } resolved)
                return resolved;
        }

        return fallback;
    }

    private string ResolveHandlerName(string handlerKeyName, string clsid)
    {
        if (!LooksLikeClsid(handlerKeyName))
            return handlerKeyName;

        return ResolveDisplayString(ReadClsidValue(clsid, string.Empty, string.Empty)) ?? clsid;
    }

    private string? ReadHandlerLibrary(string clsid)
        => ResolveSystemBinary(ExpandPath(ReadClsidValue(clsid, "InprocServer32", string.Empty)));

    private string? ReadClsidValue(string clsid, string subPath, string valueName)
    {
        foreach (var (root, classes) in Hives)
        {
            var key = $@"{classes}\CLSID\{clsid}";
            if (subPath.Length > 0) key = $@"{key}\{subPath}";

            var value = _registry.ReadString(root, key, valueName);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return null;
    }

    private static string? ResolveDisplayString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var text = raw[0] == '@' ? NativeMethods.LoadIndirectString(raw) : raw;
        if (string.IsNullOrWhiteSpace(text)) return null;

        // 한글 메뉴는 '창 열기(&S)' 처럼 액셀러레이터를 괄호로 덧붙인다. 괄호째 걷어낸다.
        var accelerator = text.IndexOf("(&", StringComparison.Ordinal);
        if (accelerator > 0)
        {
            var close = text.IndexOf(')', accelerator);
            if (close > accelerator) text = text.Remove(accelerator, close - accelerator + 1);
        }

        return text.Replace("&", string.Empty).Trim() is { Length: > 0 } cleaned ? cleaned : null;
    }

    private static string ExtractExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return string.Empty;

        var text = command.Trim();
        string path;

        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            path = end > 1 ? text[1..end] : text[1..];
        }
        else
        {
            // 따옴표 없는 경로에도 공백이 들어갈 수 있어 첫 공백으로는 자를 수 없다.
            var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            path = exe > 0 ? text[..(exe + 4)] : text.Split(' ')[0];
        }

        return ResolveSystemBinary(ExpandPath(path)) ?? string.Empty;
    }

    private static string? ExpandPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            return Environment.ExpandEnvironmentVariables(path);
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    /// <summary>
    /// <c>powershell.exe</c>, <c>mscoree.dll</c> 처럼 경로 없이 등록된 명령을 실제 파일로 되돌린다.
    /// 이렇게 해야 Windows 기본 항목인지 제대로 가려낼 수 있다.
    /// </summary>
    private static string? ResolveSystemBinary(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)) return path;

        foreach (var directory in SearchDirectories())
        {
            try
            {
                var candidate = Path.Combine(directory, path);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // PATH 에 들어 있는 잘못된 경로는 건너뛴다.
            }
        }

        return path;
    }

    private static IEnumerable<string> SearchDirectories()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.System);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return directory;
    }

    private static string Prefer(string? first, string? second)
        => !string.IsNullOrWhiteSpace(first) ? first.Trim()
            : !string.IsNullOrWhiteSpace(second) ? second.Trim()
            : string.Empty;

    private static bool LooksLikeClsid(string value)
        => value.Length is 38 && value[0] == '{' && value[^1] == '}';

    private static string? NormalizeClsid(string? value)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) || !LooksLikeClsid(text) ? null : text.ToUpperInvariant();
    }

    private static string ScopeLabel(ShellMenuScope scope) => scope switch
    {
        ShellMenuScope.AllFiles => "파일",
        ShellMenuScope.AllFilesystemObjects => "파일 · 폴더",
        ShellMenuScope.Directory => "폴더",
        ShellMenuScope.DirectoryBackground => "폴더 배경",
        ShellMenuScope.Folder => "모든 폴더",
        ShellMenuScope.Drive => "드라이브",
        _ => "기타"
    };

    /// <summary>
    /// 실행 파일이 Windows 폴더 안에 있으면 OS 가 기본 제공하는 항목으로 본다.
    /// 완벽한 판별은 불가능하지만, 사용자가 설치한 프로그램만 먼저 보여주기에는 충분하다.
    /// </summary>
    private static bool IsBuiltInPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return windows.Length > 0 && path.StartsWith(windows, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class EntryBuilder
    {
        public required string Id { get; init; }
        public required ShellMenuEntryKind Kind { get; init; }

        public string? DisplayName { get; set; }
        public string? Detail { get; set; }
        public string? SystemPath { get; set; }
        public string? Clsid { get; set; }

        public List<ShellMenuTarget> Targets { get; } = [];
        public HashSet<ShellMenuScope> Scopes { get; } = [];

        public bool AnyVisible { get; set; }
        public bool NeedsElevation { get; set; }
        public bool IsOwn { get; set; }
        public bool LooksBuiltIn { get; set; }

        public ShellMenuEntry Build()
        {
            // 명령을 전혀 찾지 못한 동사는 셸이 내부적으로 처리하는 항목이다.
            var builtIn = !IsOwn && (LooksBuiltIn || IsBuiltInPath(SystemPath) || string.IsNullOrEmpty(SystemPath));

            return new ShellMenuEntry
            {
                Id = Id,
                Kind = Kind,
                DisplayName = Prefer(DisplayName, Id),
                Detail = Prefer(Detail, "실행 명령을 확인할 수 없습니다"),
                ScopeText = string.Join(", ", Scopes.OrderBy(s => s).Select(ScopeLabel).Distinct()),
                Clsid = Clsid,
                Targets = Targets,
                IsSystem = builtIn,
                IsOwn = IsOwn,
                NeedsElevation = NeedsElevation,
                IsEnabled = AnyVisible,
                AppliedEnabled = AnyVisible
            };
        }
    }
}
