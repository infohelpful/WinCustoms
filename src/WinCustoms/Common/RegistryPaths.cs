namespace WinCustoms.Common;

/// <summary>자주 쓰는 레지스트리 경로 모음. 오타로 인한 사고를 막기 위해 한곳에 모은다.</summary>
public static class RegistryPaths
{
    // ── 탐색기 ────────────────────────────────────────────────
    public const string ExplorerAdvanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    public const string ExplorerPolicies = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
    public const string ExplorerVisualEffects = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects";
    public const string ShellExtensionsBlocked = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";

    /// <summary>Windows 11 새 컨텍스트 메뉴를 담당하는 셸 확장 CLSID.</summary>
    public const string ClassicContextMenuClsid = "{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";

    public const string ClassicContextMenuKey =
        @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";

    public const string ClassicContextMenuInprocKey =
        @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32";

    /// <summary>탐색기 탐색 창의 '홈' 노드.</summary>
    public const string HomeNodeKey = @"Software\Classes\CLSID\{f874310e-b6b7-47dc-bc84-b9e6b38f5903}";

    /// <summary>탐색기 탐색 창의 '갤러리' 노드.</summary>
    public const string GalleryNodeKey = @"Software\Classes\CLSID\{e88865ea-0e1c-4e20-9aa6-edcd0212c87c}";

    public const string PinnedToNameSpaceTree = "System.IsPinnedToNameSpaceTree";

    // ── 사용자 클래스(우클릭 메뉴) ─────────────────────────────
    public const string ClassesRootUser = @"Software\Classes";
    public const string ClassesRootMachine = @"SOFTWARE\Classes";
    public const string AllFilesShell = @"Software\Classes\*\shell";
    public const string DirectoryShell = @"Software\Classes\Directory\shell";
    public const string DirectoryBackgroundShell = @"Software\Classes\Directory\Background\shell";
    public const string DriveShell = @"Software\Classes\Drive\shell";

    /// <summary>WinCustoms 가 만든 우클릭 항목 접두사. 삭제 시 자신의 항목만 안전하게 골라낼 수 있다.</summary>
    public const string ContextEntryPrefix = "WinCustoms.";

    /// <summary>셸 동사 키에 이 값이 있으면 메뉴에 그려지지 않는다. 값을 지우면 그대로 되살아난다.</summary>
    public const string LegacyDisableValue = "LegacyDisable";

    /// <summary>설계상 메뉴에 노출되지 않고 프로그램에서만 호출되는 동사임을 뜻한다.</summary>
    public const string ProgrammaticAccessOnlyValue = "ProgrammaticAccessOnly";

    /// <summary>shellex 컨텍스트 메뉴 처리기가 등록되는 하위 경로.</summary>
    public const string ContextMenuHandlersSuffix = @"shellex\ContextMenuHandlers";

    // ── 작업 표시줄 / 검색 ─────────────────────────────────────
    public const string SearchKey = @"Software\Microsoft\Windows\CurrentVersion\Search";
    public const string SearchSettings = @"Software\Microsoft\Windows\CurrentVersion\SearchSettings";
    public const string WindowsSearchPolicy = @"SOFTWARE\Policies\Microsoft\Windows\Windows Search";
    public const string ExplorerPolicyKeyUser = @"Software\Policies\Microsoft\Windows\Explorer";
    public const string StartPolicyDevice = @"SOFTWARE\Microsoft\PolicyManager\current\device\Start";
    public const string StartMenuInternet = @"SOFTWARE\Clients\StartMenuInternet";
    public const string MicrosoftEdgeProtocol = @"Software\Classes\microsoft-edge";
    public const string MsEdgeHtmClass = @"Software\Classes\MSEdgeHTM";

    // ── 개인정보 ──────────────────────────────────────────────
    public const string DataCollectionPolicy = @"SOFTWARE\Policies\Microsoft\Windows\DataCollection";
    public const string AdvertisingInfo = @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo";
    public const string AdvertisingInfoPolicy = @"SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo";
    public const string ContentDeliveryManager = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    public const string CloudContentPolicy = @"SOFTWARE\Policies\Microsoft\Windows\CloudContent";
    public const string PrivacyKey = @"Software\Microsoft\Windows\CurrentVersion\Privacy";
    public const string SystemPolicy = @"SOFTWARE\Policies\Microsoft\Windows\System";
    public const string LocationAndSensorsPolicy = @"SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors";
    public const string InputPersonalization = @"Software\Microsoft\InputPersonalization";
    public const string TrainedDataStore = @"Software\Microsoft\InputPersonalization\TrainedDataStore";
    public const string PersonalizationSettings = @"Software\Microsoft\Personalization\Settings";
    public const string OnlineSpeechPrivacy = @"Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy";
    public const string ClipboardUser = @"Software\Microsoft\Clipboard";
    public const string FindMyDevicePolicy = @"SOFTWARE\Policies\Microsoft\FindMyDevice";
    public const string SmartClipboard = @"Software\Microsoft\Windows\CurrentVersion\SmartActionPlatform\SmartClipboard";
    public const string CopilotPolicyUser = @"Software\Policies\Microsoft\Windows\WindowsCopilot";
    public const string CopilotPolicyMachine = @"SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot";
    public const string WindowsAiPolicy = @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI";
    public const string EdgePolicy = @"SOFTWARE\Policies\Microsoft\Edge";

    // ── 성능 ──────────────────────────────────────────────────
    public const string DesktopKey = @"Control Panel\Desktop";
    public const string WindowMetrics = @"Control Panel\Desktop\WindowMetrics";
    public const string DwmKey = @"Software\Microsoft\Windows\DWM";
    public const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    public const string PowerSchemesKey = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";
    public const string DriverSearching = @"SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching";
    public const string WindowsUpdatePolicy = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    public const string DeliveryOptimizationPolicy = @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization";
    public const string DeviceMetadata = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Device Metadata";
    public const string SystemRestore = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
    public const string GameDvrPolicy = @"SOFTWARE\Policies\Microsoft\Windows\GameDVR";
    public const string GameDvrUser = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
    public const string GameConfigStore = @"System\GameConfigStore";
    public const string BackgroundAccessApplications = @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications";
    public const string FileSystemKey = @"SYSTEM\CurrentControlSet\Control\FileSystem";
    public const string PrefetchParameters = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters";
    public const string MultimediaSystemProfile = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    public const string WindowsErrorReporting = @"SOFTWARE\Microsoft\Windows\Windows Error Reporting";
    public const string RemoteAssistance = @"SYSTEM\CurrentControlSet\Control\Remote Assistance";

    // ── 전원 구성표 GUID ──────────────────────────────────────
    public const string UltimatePerformanceGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    public const string BalancedGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";

    // ── WinCustoms 자체 설정 ──────────────────────────────────
    public const string AppSettingsKey = @"Software\WinCustoms";
}
