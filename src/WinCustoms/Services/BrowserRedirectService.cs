using Microsoft.Win32;
using WinCustoms.Common;

namespace WinCustoms.Services;

public sealed record InstalledBrowser(string Name, string ExecutablePath)
{
    public bool IsEdge =>
        Name.Contains("Edge", StringComparison.OrdinalIgnoreCase)
        || ExecutablePath.Contains("msedge", StringComparison.OrdinalIgnoreCase);
}

public interface IBrowserRedirectService
{
    /// <summary>시작 메뉴에 등록된 브라우저 목록.</summary>
    IReadOnlyList<InstalledBrowser> ListInstalled();

    /// <summary>microsoft-edge: / MSEdgeHTM 호출을 선택한 브라우저로 넘긴다.</summary>
    Task ApplyRedirectAsync(InstalledBrowser browser, CancellationToken ct = default);

    Task ClearRedirectAsync(CancellationToken ct = default);

    bool IsRedirectActive();

    string? CurrentTargetPath();
}

/// <summary>
/// 시작 검색·시스템 링크가 microsoft-edge: 프로토콜로 Edge 를 강제할 때
/// HKCU 클래스 등록으로 다른 브라우저를 가리키게 한다.
/// Windows Update 가 되돌릴 수 있어, 웹 검색 끄기와 함께 쓰는 편이 안정적이다.
/// </summary>
public sealed class BrowserRedirectService(IRegistryService registry) : IBrowserRedirectService
{
    private const string StoredPathValue = "SearchBrowserPath";

    private readonly IRegistryService _registry = registry;

    public IReadOnlyList<InstalledBrowser> ListInstalled()
    {
        var found = new Dictionary<string, InstalledBrowser>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in new[] { RegistryRoot.LocalMachine, RegistryRoot.CurrentUser })
        {
            foreach (var client in _registry.GetSubKeyNames(root, RegistryPaths.StartMenuInternet))
            {
                var command = _registry.ReadString(
                    root, $@"{RegistryPaths.StartMenuInternet}\{client}\shell\open\command", string.Empty);

                var path = ExtractExecutable(command);
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;

                var name = _registry.ReadString(
                    root, $@"{RegistryPaths.StartMenuInternet}\{client}", string.Empty);

                if (string.IsNullOrWhiteSpace(name))
                    name = Path.GetFileNameWithoutExtension(path);

                found[path] = new InstalledBrowser(name.Trim(), path);
            }
        }

        return found.Values
            .OrderBy(b => b.IsEdge)
            .ThenBy(b => b.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task ApplyRedirectAsync(InstalledBrowser browser, CancellationToken ct = default)
    {
        if (!File.Exists(browser.ExecutablePath))
            throw new FileNotFoundException("선택한 브라우저를 찾을 수 없습니다.", browser.ExecutablePath);

        var command = $@"""{browser.ExecutablePath}"" ""%1""";
        var ops = new List<RegistryOperation>
        {
            RegistryOperation.CreateKey(RegistryRoot.CurrentUser, RegistryPaths.MicrosoftEdgeProtocol),
            RegistryOperation.Set(RegistryRoot.CurrentUser, RegistryPaths.MicrosoftEdgeProtocol,
                "URL Protocol", RegistryValueKind.String, string.Empty),
            RegistryOperation.CreateKey(RegistryRoot.CurrentUser, $@"{RegistryPaths.MicrosoftEdgeProtocol}\shell\open\command"),
            RegistryOperation.Set(RegistryRoot.CurrentUser, $@"{RegistryPaths.MicrosoftEdgeProtocol}\shell\open\command",
                string.Empty, RegistryValueKind.String, command),

            RegistryOperation.CreateKey(RegistryRoot.CurrentUser, RegistryPaths.MsEdgeHtmClass),
            RegistryOperation.CreateKey(RegistryRoot.CurrentUser, $@"{RegistryPaths.MsEdgeHtmClass}\shell\open\command"),
            RegistryOperation.Set(RegistryRoot.CurrentUser, $@"{RegistryPaths.MsEdgeHtmClass}\shell\open\command",
                string.Empty, RegistryValueKind.String, command),

            RegistryOperation.CreateKey(RegistryRoot.CurrentUser, RegistryPaths.AppSettingsKey),
            RegistryOperation.Set(RegistryRoot.CurrentUser, RegistryPaths.AppSettingsKey,
                StoredPathValue, RegistryValueKind.String, browser.ExecutablePath)
        };

        await _registry.ExecuteAsync(ops, ct).ConfigureAwait(false);
        NativeMethods.NotifyShellAssociationChanged();
    }

    public async Task ClearRedirectAsync(CancellationToken ct = default)
    {
        var ops = new List<RegistryOperation>
        {
            RegistryOperation.DeleteKeyTree(RegistryRoot.CurrentUser, RegistryPaths.MicrosoftEdgeProtocol),
            RegistryOperation.DeleteKeyTree(RegistryRoot.CurrentUser, RegistryPaths.MsEdgeHtmClass),
            RegistryOperation.DeleteValue(RegistryRoot.CurrentUser, RegistryPaths.AppSettingsKey, StoredPathValue)
        };

        await _registry.ExecuteAsync(ops, ct).ConfigureAwait(false);
        NativeMethods.NotifyShellAssociationChanged();
    }

    public bool IsRedirectActive()
    {
        var command = _registry.ReadString(
            RegistryRoot.CurrentUser, $@"{RegistryPaths.MicrosoftEdgeProtocol}\shell\open\command", string.Empty);

        if (string.IsNullOrWhiteSpace(command)) return false;

        var path = ExtractExecutable(command);
        return !string.IsNullOrEmpty(path)
               && File.Exists(path)
               && !path.Contains("msedge", StringComparison.OrdinalIgnoreCase);
    }

    public string? CurrentTargetPath()
        => _registry.ReadString(RegistryRoot.CurrentUser, RegistryPaths.AppSettingsKey, StoredPathValue);

    private static string ExtractExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return string.Empty;

        var text = command.Trim();
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : text[1..];
        }

        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? text[..(exe + 4)] : text.Split(' ')[0];
    }
}
