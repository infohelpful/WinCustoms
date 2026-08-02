using WinCustoms.Models;

namespace WinCustoms.Services.Catalog;

public interface ITweakCatalog
{
    IReadOnlyList<TweakItem> GetTweaks(TweakCategory category);

    IReadOnlyList<TweakItem> All { get; }

    TweakItem? FindById(string id);
}

/// <summary>
/// 모든 트윅 정의의 단일 출처.
/// 카테고리별 정의는 partial 파일로 나눠 두었다(TweakCatalog.Explorer.cs 등).
/// </summary>
public sealed partial class TweakCatalog : ITweakCatalog
{
    private readonly TweakFactory _factory;
    private readonly IRegistryService _registry;
    private readonly IShellService _shell;
    private readonly IDialogService _dialog;
    private readonly IMaintenanceService _maintenance;
    private readonly IBrowserRedirectService _browsers;

    private readonly Dictionary<TweakCategory, IReadOnlyList<TweakItem>> _cache = [];

    public TweakCatalog(
        TweakFactory factory,
        IRegistryService registry,
        IShellService shell,
        IDialogService dialog,
        IMaintenanceService maintenance,
        IBrowserRedirectService browsers)
    {
        _factory = factory;
        _registry = registry;
        _shell = shell;
        _dialog = dialog;
        _maintenance = maintenance;
        _browsers = browsers;
    }

    public IReadOnlyList<TweakItem> GetTweaks(TweakCategory category)
    {
        if (_cache.TryGetValue(category, out var cached))
            return cached;

        IReadOnlyList<TweakItem> built = category switch
        {
            TweakCategory.Explorer => BuildExplorerTweaks(),
            TweakCategory.Taskbar => BuildTaskbarTweaks(),
            TweakCategory.Privacy => BuildPrivacyTweaks(),
            TweakCategory.Performance => BuildPerformanceTweaks(),
            TweakCategory.PowerTools => BuildPowerToolTweaks(),
            _ => []
        };

        _cache[category] = built;
        return built;
    }

    public IReadOnlyList<TweakItem> All => Enum.GetValues<TweakCategory>()
        .SelectMany(GetTweaks)
        .ToList();

    public TweakItem? FindById(string id)
        => All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));
}
