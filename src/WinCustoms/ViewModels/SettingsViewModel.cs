using System.Diagnostics;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.Win32;
using WinCustoms.Common;
using WinCustoms.Services;
using WinCustoms.Services.Catalog;

namespace WinCustoms.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IRegistryService _registry;
    private readonly IShellService _shell;
    private readonly IElevationService _elevation;
    private readonly ITweakCatalog _catalog;

    private const string ThemeValueName = "AppTheme";

    public SettingsViewModel(
        IRegistryService registry,
        IShellService shell,
        IElevationService elevation,
        ITweakCatalog catalog)
    {
        _registry = registry;
        _shell = shell;
        _elevation = elevation;
        _catalog = catalog;

        SelectedThemeIndex = LoadThemeIndex();
        _loaded = true;
    }

    /// <summary>저장된 값을 되읽는 동안에는 다시 저장하지 않도록 하는 플래그.</summary>
    private readonly bool _loaded;

    /// <summary>0 = 시스템 설정, 1 = 라이트, 2 = 다크</summary>
    [ObservableProperty]
    public partial int SelectedThemeIndex { get; set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    public string BackupFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinCustoms", "Backups");

    public string ElevationText => _elevation.IsElevated
        ? "관리자 권한으로 실행 중입니다."
        : "표준 권한으로 실행 중입니다. HKLM 을 수정하는 트윅에서만 UAC 승인을 요청합니다.";

    public string AppliedSummary
    {
        get
        {
            var all = _catalog.All.Where(t => t.IsToggle).ToList();
            var applied = all.Count(t => t.IsApplied);
            return $"전체 {all.Count}개 트윅 중 {applied}개가 적용되어 있습니다.";
        }
    }

    public string RuntimeInfo =>
        $"WinCustoms {GetAppVersion()} · {Environment.OSVersion.VersionString} · "
        + (Environment.Is64BitProcess ? "x64" : "x86");

    internal static string GetAppVersion()
    {
        var asm = typeof(App).Assembly;
        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // SDK 가 붙이는 +gitsha 등은 UI 에서 숨긴다.
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        var v = asm.GetName().Version;
        return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    partial void OnSelectedThemeIndexChanged(int value)
    {
        ApplyTheme(value);

        if (_loaded) SaveThemeIndex(value);
    }

    private int LoadThemeIndex()
        => _registry.ReadDword(RegistryRoot.CurrentUser, RegistryPaths.AppSettingsKey, ThemeValueName) ?? 0;

    private void SaveThemeIndex(int value)
    {
        try
        {
            using var key = RegistryRoot.CurrentUser.Open().CreateSubKey(RegistryPaths.AppSettingsKey, writable: true);
            key?.SetValue(ThemeValueName, value, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WinCustoms] 테마 설정을 저장하지 못했습니다: {ex.Message}");
        }
    }

    public static void ApplyTheme(int index)
    {
        if (App.Window?.Content is not FrameworkElement root) return;

        root.RequestedTheme = index switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }

    [RelayCommand]
    private void OpenBackupFolder()
    {
        Directory.CreateDirectory(BackupFolder);
        Process.Start(new ProcessStartInfo(BackupFolder) { UseShellExecute = true })?.Dispose();
    }

    [RelayCommand]
    private async Task RestartExplorerAsync(CancellationToken ct)
    {
        StatusMessage = "탐색기를 다시 시작하는 중...";
        await _shell.RestartExplorerAsync(ct);
        StatusMessage = "탐색기를 다시 시작했습니다.";
    }

    [RelayCommand]
    private async Task OpenProjectPageAsync()
        => await _shell.OpenUrlAsync("https://learn.microsoft.com/windows/apps/winui/winui3/");

    [RelayCommand]
    private void Refresh()
    {
        OnPropertyChanged(nameof(AppliedSummary));
        OnPropertyChanged(nameof(RuntimeInfo));
        StatusMessage = null;
    }
}
