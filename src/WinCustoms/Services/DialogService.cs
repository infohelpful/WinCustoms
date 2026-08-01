using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace WinCustoms.Services;

public interface IDialogService
{
    /// <summary>ContentDialog 를 띄우려면 XamlRoot 가 필요하다. MainWindow 가 최초 1회 설정한다.</summary>
    XamlRoot? XamlRoot { get; set; }

    Task ShowMessageAsync(string title, string message);

    Task<bool> ConfirmAsync(string title, string message, string primaryText = "계속", string closeText = "취소");

    /// <summary>실행 파일 선택 대화 상자. 취소하면 null.</summary>
    Task<string?> PickExecutableAsync();
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
            var dialog = new ContentDialog
            {
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "확인",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };

            await dialog.ShowAsync();
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
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> PickExecutableAsync()
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
            ViewMode = PickerViewMode.List
        };

        picker.FileTypeFilter.Add(".exe");
        picker.FileTypeFilter.Add(".bat");
        picker.FileTypeFilter.Add(".cmd");
        picker.FileTypeFilter.Add(".lnk");

        // 언패키지 앱에서는 피커에 소유자 창 핸들을 직접 지정해야 한다.
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }
}
