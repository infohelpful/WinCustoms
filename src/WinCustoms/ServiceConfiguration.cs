using Microsoft.Extensions.DependencyInjection;
using WinCustoms.Models;
using WinCustoms.Services;
using WinCustoms.Services.Catalog;
using WinCustoms.ViewModels;

namespace WinCustoms;

internal static class ServiceConfiguration
{
    public static IServiceProvider Build()
    {
        var services = new ServiceCollection();

        // ── 서비스 ────────────────────────────────────────────────
        services.AddSingleton<IElevationService, ElevationService>();
        services.AddSingleton<IRegistryService, RegistryService>();
        services.AddSingleton<IShellService, ShellService>();
        services.AddSingleton<IMaintenanceService, MaintenanceService>();
        services.AddSingleton<IAppxService, AppxService>();
        services.AddSingleton<IContextMenuService, ContextMenuService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<ITweakEngine, TweakEngine>();

        services.AddSingleton<TweakFactory>();
        services.AddSingleton<ITweakCatalog, TweakCatalog>();

        // ── 뷰모델 ────────────────────────────────────────────────
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<ExplorerViewModel>();
        services.AddSingleton<TaskbarViewModel>();
        services.AddSingleton<PrivacyViewModel>();
        services.AddSingleton<PerformanceViewModel>();
        services.AddSingleton<PowerToolsViewModel>();
        services.AddSingleton<ContextMenuEditorViewModel>();
        services.AddSingleton<DebloatViewModel>();
        services.AddSingleton<SettingsViewModel>();

        services.AddSingleton<TweakPageViewModelLocator>();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = false,
            ValidateScopes = false
        });
    }
}
