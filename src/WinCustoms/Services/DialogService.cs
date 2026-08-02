using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinCustoms.Common;

namespace WinCustoms.Services;

public interface IDialogService
{
    /// <summary>ContentDialog 를 띄우려면 XamlRoot 가 필요하다. MainWindow 가 최초 1회 설정한다.</summary>
    XamlRoot? XamlRoot { get; set; }

    Task ShowMessageAsync(string title, string message);

    Task<bool> ConfirmAsync(string title, string message, string primaryText = "계속", string closeText = "취소");

    /// <summary>실행 파일 선택 대화 상자. 취소하면 null.</summary>
    Task<string?> PickExecutableAsync();

    /// <summary>폴더 선택. 취소하면 null.</summary>
    Task<string?> PickFolderAsync();

    /// <summary>WIM 파일 열기. 취소하면 null.</summary>
    Task<string?> PickWimFileAsync();

    /// <summary>WIM 저장 경로 선택. 취소하면 null.</summary>
    Task<string?> PickSaveWimAsync(string suggestedFileName);

    /// <summary>Windows ISO 열기.</summary>
    Task<string?> PickIsoFileAsync();

    /// <summary>ISO 저장 경로.</summary>
    Task<string?> PickSaveIsoAsync(string suggestedFileName);

    /// <summary>목록에서 하나를 고른다. 취소하면 null.</summary>
    Task<T?> PickOptionAsync<T>(string title, string message, IReadOnlyList<(string Label, T Value)> options, string primaryText = "선택");
}

public sealed class DialogService : IDialogService
{
    // 여러 다이얼로그가 동시에 열리면 WinUI 가 예외를 던지므로 직렬화한다.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public XamlRoot? XamlRoot { get; set; }

    public async Task ShowMessageAsync(string title, string message)
    {
        if (XamlRoot is null) return;

        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            await UiThread.InvokeAsync(async () =>
            {
                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    CloseButtonText = "확인",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = XamlRoot
                };

                await dialog.ShowAsync();
            }).ConfigureAwait(true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ConfirmAsync(string title, string message, string primaryText = "계속", string closeText = "취소")
    {
        if (XamlRoot is null) return false;

        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            return await UiThread.InvokeAsync(async () =>
            {
                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    PrimaryButtonText = primaryText,
                    CloseButtonText = closeText,
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = XamlRoot
                };

                return await dialog.ShowAsync() == ContentDialogResult.Primary;
            }).ConfigureAwait(true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<string?> PickExecutableAsync()
        => PickOpenAsync(
            "실행 파일 선택",
            "실행 파일",
            "*.exe;*.bat;*.cmd;*.lnk",
            "exe");

    public Task<string?> PickFolderAsync()
        => RunNativeAsync(() => NativeFileDialog.PickFolder(App.WindowHandle, "폴더 선택"));

    public Task<string?> PickWimFileAsync()
        => PickOpenAsync("WIM 파일 선택", "Windows 이미지 (*.wim)", "*.wim", "wim");

    public Task<string?> PickSaveWimAsync(string suggestedFileName)
        => PickSaveAsync(
            "WIM 저장",
            "Windows 이미지 (*.wim)",
            "*.wim",
            string.IsNullOrWhiteSpace(suggestedFileName)
                ? $"WinCustoms-Backup-{DateTime.Now:yyyyMMdd}.wim"
                : EnsureExtension(suggestedFileName, ".wim"),
            "wim");

    public Task<string?> PickIsoFileAsync()
        => PickOpenAsync("순정 Windows ISO 선택", "디스크 이미지 (*.iso)", "*.iso", "iso");

    public Task<string?> PickSaveIsoAsync(string suggestedFileName)
        => PickSaveAsync(
            "커스텀 ISO 저장",
            "디스크 이미지 (*.iso)",
            "*.iso",
            string.IsNullOrWhiteSpace(suggestedFileName)
                ? $"WinCustoms-Win11-{DateTime.Now:yyyyMMdd}.iso"
                : EnsureExtension(suggestedFileName, ".iso"),
            "iso");

    private Task<string?> PickOpenAsync(string title, string filterDescription, string filterPattern, string defaultExt)
        => RunNativeAsync(() =>
            NativeFileDialog.OpenFile(App.WindowHandle, title, filterDescription, filterPattern, defaultExt));

    private Task<string?> PickSaveAsync(
        string title, string filterDescription, string filterPattern, string suggestedFileName, string defaultExt)
        => RunNativeAsync(() =>
            NativeFileDialog.SaveFile(
                App.WindowHandle, title, filterDescription, filterPattern, suggestedFileName, defaultExt));

    private async Task<string?> RunNativeAsync(Func<string?> show)
    {
        try
        {
            return await UiThread.InvokeAsync(() => Task.FromResult(show())).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            CrashLog.Write("file-dialog", ex);
            await ShowMessageAsync("파일 선택 실패", ex.Message).ConfigureAwait(true);
            return null;
        }
    }

    private static string EnsureExtension(string fileName, string extension)
    {
        if (fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            return fileName;
        return fileName + extension;
    }

    public async Task<T?> PickOptionAsync<T>(
        string title,
        string message,
        IReadOnlyList<(string Label, T Value)> options,
        string primaryText = "선택")
    {
        if (XamlRoot is null || options.Count == 0) return default;

        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            return await UiThread.InvokeAsync(async () =>
            {
                var combo = new ComboBox
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    MinWidth = 320
                };

                foreach (var (label, value) in options)
                    combo.Items.Add(new ComboBoxItem { Content = label, Tag = value });

                combo.SelectedIndex = 0;

                var panel = new StackPanel { Spacing = 12 };
                panel.Children.Add(new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap
                });
                panel.Children.Add(combo);

                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = panel,
                    PrimaryButtonText = primaryText,
                    CloseButtonText = "취소",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = XamlRoot
                };

                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                    return default;

                return combo.SelectedItem is ComboBoxItem { Tag: T chosen } ? chosen : default;
            }).ConfigureAwait(true);
        }
        finally
        {
            _gate.Release();
        }
    }
}
