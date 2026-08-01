using Microsoft.Win32;
using WinCustoms.Common;
using WinCustoms.Models;

namespace WinCustoms.Services.Catalog;

public sealed partial class TweakCatalog
{
    // HKCU\Software\Classes 아래에만 등록하므로 관리자 권한 없이 추가/제거된다.
    private const string TakeOwnershipKeyName = RegistryPaths.ContextEntryPrefix + "TakeOwnership";
    private const string TerminalKeyName = RegistryPaths.ContextEntryPrefix + "OpenTerminal";

    /// <summary>takeown + icacls 를 관리자로 실행한다. *S-1-3-4 는 'OWNER RIGHTS' 의 언어 독립 SID.</summary>
    private const string TakeOwnershipFileCommand =
        @"powershell.exe -NoProfile -WindowStyle Hidden -Command ""Start-Process cmd.exe -ArgumentList '/c takeown /f \""%1\"" && icacls \""%1\"" /grant *S-1-3-4:F /c /l' -Verb runAs""";

    private const string TakeOwnershipDirectoryCommand =
        @"powershell.exe -NoProfile -WindowStyle Hidden -Command ""Start-Process cmd.exe -ArgumentList '/c takeown /f \""%1\"" /r /d y && icacls \""%1\"" /grant *S-1-3-4:F /t /c /l /q' -Verb runAs""";

    private IReadOnlyList<TweakItem> BuildPowerToolTweaks() =>
    [
        TakeOwnershipTweak(),
        OpenTerminalHereTweak(),

        _factory.Action(
            id: "tools.clean-temp",
            title: "임시 파일 즉시 비우기",
            description: "사용자 임시 폴더, 인터넷 캐시, C:\\Windows\\Temp 를 정리합니다. "
                       + "사용 중인 파일은 자동으로 건너뛰므로 실행 중인 프로그램에 영향을 주지 않습니다.",
            category: TweakCategory.PowerTools,
            actionText: "지금 정리",
            run: async ct =>
            {
                // ContentDialog 는 UI 스레드에서만 열 수 있으므로 ConfigureAwait(true) 를 유지한다.
                var report = await _maintenance.CleanTempFilesAsync(ct).ConfigureAwait(true);
                LastCleanupReport = report;

                await _dialog.ShowMessageAsync(
                    "임시 파일 정리 완료",
                    $"{report.FilesDeleted}개 파일 · {report.BytesFreedText} 확보"
                    + (report.Skipped > 0 ? $"\n(사용 중이어서 건너뛴 항목 {report.Skipped}개)" : string.Empty));
            },
            requiresAdmin: true,
            risk: TweakRisk.Moderate),

        _factory.Action(
            id: "tools.restore-point",
            title: "시스템 복원 지점 만들기",
            description: "현재 상태를 복원 지점으로 저장합니다. 트윅을 적용하기 전에 한 번 눌러 두면 가장 확실한 안전장치가 됩니다. "
                       + "시스템 보호가 꺼져 있으면 자동으로 켭니다.",
            category: TweakCategory.PowerTools,
            actionText: "복원 지점 생성",
            run: ct => _maintenance.CreateRestorePointAsync(
                $"WinCustoms {DateTime.Now:yyyy-MM-dd HH:mm}", ct),
            requiresAdmin: true,
            risk: TweakRisk.Safe)
    ];

    /// <summary>가장 최근 임시 파일 정리 결과. UI 에서 안내 문구로 사용한다.</summary>
    public CleanupReport? LastCleanupReport { get; private set; }

    private TweakItem TakeOwnershipTweak()
    {
        var fileKey = $@"{RegistryPaths.AllFilesShell}\{TakeOwnershipKeyName}";
        var directoryKey = $@"{RegistryPaths.DirectoryShell}\{TakeOwnershipKeyName}";

        return _factory.FromRegistry(
            id: "tools.take-ownership",
            title: "우클릭에 '소유권 가져오기' 추가",
            description: "접근이 거부된 파일이나 폴더의 소유자를 현재 계정으로 바꾸고 모든 권한을 부여합니다. "
                       + "메뉴를 누르면 그때 UAC 확인 창이 뜹니다.",
            category: TweakCategory.PowerTools,
            specs:
            [
                // 파일용
                Default(fileKey, "소유권 가져오기(&T)"),
                Empty(fileKey, "HasLUAShield"),
                Empty(fileKey, "NoWorkingDirectory"),
                Str(fileKey, "Position", "middle"),
                Default($@"{fileKey}\command", TakeOwnershipFileCommand),
                Str($@"{fileKey}\command", "IsolatedCommand", TakeOwnershipFileCommand),

                // 폴더용 (하위 항목까지 재귀 적용)
                Default(directoryKey, "소유권 가져오기(&T)"),
                Empty(directoryKey, "HasLUAShield"),
                Empty(directoryKey, "NoWorkingDirectory"),
                Str(directoryKey, "Position", "middle"),
                Default($@"{directoryKey}\command", TakeOwnershipDirectoryCommand),
                Str($@"{directoryKey}\command", "IsolatedCommand", TakeOwnershipDirectoryCommand)
            ],
            deleteKeysOnRestore:
            [
                (RegistryRoot.CurrentUser, fileKey),
                (RegistryRoot.CurrentUser, directoryKey)
            ],
            risk: TweakRisk.Moderate,
            detect: () => _registry.KeyExists(RegistryRoot.CurrentUser, $@"{fileKey}\command"));
    }

    private TweakItem OpenTerminalHereTweak()
    {
        var directoryKey = $@"{RegistryPaths.DirectoryShell}\{TerminalKeyName}";
        var backgroundKey = $@"{RegistryPaths.DirectoryBackgroundShell}\{TerminalKeyName}";
        var driveKey = $@"{RegistryPaths.DriveShell}\{TerminalKeyName}";

        var (label, command, backgroundCommand, icon) = ResolveTerminalCommand();

        return _factory.FromRegistry(
            id: "tools.open-terminal-here",
            title: $"우클릭에 '{label}' 추가",
            description: "폴더를 우클릭하거나 폴더 안 빈 공간을 우클릭하면 해당 경로에서 곧바로 터미널이 열립니다. "
                       + "Windows Terminal 이 설치되어 있으면 자동으로 사용하고, 없으면 PowerShell 로 대체합니다.",
            category: TweakCategory.PowerTools,
            specs:
            [
                Default(directoryKey, label),
                Str(directoryKey, "Icon", icon),
                Default($@"{directoryKey}\command", command),

                Default(backgroundKey, label),
                Str(backgroundKey, "Icon", icon),
                Default($@"{backgroundKey}\command", backgroundCommand),

                Default(driveKey, label),
                Str(driveKey, "Icon", icon),
                Default($@"{driveKey}\command", command)
            ],
            deleteKeysOnRestore:
            [
                (RegistryRoot.CurrentUser, directoryKey),
                (RegistryRoot.CurrentUser, backgroundKey),
                (RegistryRoot.CurrentUser, driveKey)
            ],
            detect: () => _registry.KeyExists(RegistryRoot.CurrentUser, $@"{directoryKey}\command"));
    }

    /// <summary>Windows Terminal 이 있으면 wt.exe, 없으면 powershell.exe 를 사용한다.</summary>
    private static (string Label, string Command, string BackgroundCommand, string Icon) ResolveTerminalCommand()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var wt = Path.Combine(localAppData, @"Microsoft\WindowsApps\wt.exe");

        if (File.Exists(wt))
        {
            return (
                "여기서 터미널 열기(&W)",
                $@"""{wt}"" -d ""%V""",
                $@"""{wt}"" -d ""%V""",
                wt);
        }

        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe");

        // %V 는 폴더 자체와 폴더 배경 모두에서 올바른 경로로 확장된다.
        var command = $@"""{powershell}"" -NoExit -Command ""Set-Location -LiteralPath '%V'""";
        return ("여기서 PowerShell 열기(&W)", command, command, powershell);
    }

    // ── 스펙 작성용 짧은 헬퍼 ────────────────────────────────────

    private static RegistryValueSpec Default(string subKey, string value)
        => new(RegistryRoot.CurrentUser, subKey, string.Empty, RegistryValueKind.String, value);

    private static RegistryValueSpec Str(string subKey, string name, string value)
        => new(RegistryRoot.CurrentUser, subKey, name, RegistryValueKind.String, value);

    private static RegistryValueSpec Empty(string subKey, string name)
        => new(RegistryRoot.CurrentUser, subKey, name, RegistryValueKind.String, string.Empty);
}
