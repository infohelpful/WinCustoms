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
            description: "임시 파일이 쌓이면 디스크를 잡아먹습니다. 정리하면 공간을 바로 되찾습니다.",
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
            description: "복원 지점이 없으면 잘못 건드릴 때 되돌리기 어렵습니다. 만들면 문제 생겼을 때 이전 상태로 돌아갈 수 있습니다.",
            category: TweakCategory.PowerTools,
            actionText: "복원 지점 생성",
            run: async ct =>
            {
                await _maintenance.CreateRestorePointAsync(
                    $"WinCustoms {DateTime.Now:yyyy-MM-dd HH:mm}", ct).ConfigureAwait(true);

                await _dialog.ShowMessageAsync(
                    "복원 지점 생성 완료",
                    "시스템 복원 지점을 만들었습니다.\n문제가 생기면 Windows 설정의 '복원'에서 이 지점으로 되돌릴 수 있습니다.");
            },
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
            description: "권한이 막힌 파일은 지우거나 고치기 어렵습니다. 켜면 우클릭으로 소유권을 바로 가져올 수 있습니다.",
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
            description: "터미널을 따로 열고 경로를 찾으면 시간이 걸립니다. 켜면 폴더 우클릭으로 바로 열 수 있습니다.",
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
        // wt.exe 는 Windows 10/11 기본 앱 실행 별칭에 등록되어 있으며 모든 사용자 환경에서 동작한다.
        const string wtCommand = @"wt.exe -d ""%V""";
        const string wtIcon = @"wt.exe";

        return (
            "여기서 터미널 열기(&W)",
            wtCommand,
            wtCommand,
            wtIcon);
    }

    // ── 스펙 작성용 짧은 헬퍼 ────────────────────────────────────

    private static RegistryValueSpec Default(string subKey, string value)
        => new(RegistryRoot.CurrentUser, subKey, string.Empty, RegistryValueKind.String, value);

    private static RegistryValueSpec Str(string subKey, string name, string value)
        => new(RegistryRoot.CurrentUser, subKey, name, RegistryValueKind.String, value);

    private static RegistryValueSpec Empty(string subKey, string name)
        => new(RegistryRoot.CurrentUser, subKey, name, RegistryValueKind.String, string.Empty);
}
