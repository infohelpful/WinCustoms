using Microsoft.UI.Xaml.Controls;
using WinCustoms.ViewModels;

namespace WinCustoms.Views;

public sealed partial class SystemBackupPage : Page
{
    public SystemBackupViewModel ViewModel { get; }

    public SystemBackupPage()
    {
        ViewModel = App.GetService<SystemBackupViewModel>();
        InitializeComponent();
    }
}
