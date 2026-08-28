using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;

namespace WinCustoms.Services;

/// <summary>제거 후보로 제시할 기본 제공 앱 및 기능.</summary>
public sealed partial class AppxPackageInfo : ObservableObject
{
    public required string PackageName { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }

    /// <summary>
    /// 같은 앱이 윈도우 버전에 따라 다른 패키지 이름으로 배포되는 경우가 있다.
    /// 예: 빠른 지원은 Microsoft.QuickAssist 였다가 MicrosoftCorporationII.QuickAssist 로 바뀌었다.
    /// </summary>
    public string[] Aliases { get; init; } = [];

    /// <summary>제거해도 대부분의 사용자에게 문제가 없는지 여부. false 면 기본 선택되지 않는다.</summary>
    public bool RecommendedForRemoval { get; init; }

    /// <summary>실제로 설치되어 있는 것으로 확인된 패키지/기능 이름.</summary>
    public string? InstalledPackageName { get; set; }

    public IEnumerable<string> CandidateNames => Aliases.Prepend(PackageName);

    [ObservableProperty]
    public partial bool IsInstalled { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? LastError { get; set; }
}

public interface IAppxService
{
    /// <summary>제거 후보 목록을 설치 여부와 함께 가져온다.</summary>
    Task<IReadOnlyList<AppxPackageInfo>> LoadCatalogAsync(CancellationToken ct = default);

    /// <summary>로컬 설치 여부 확인 없이 화이트리스트 카탈로그만 반환(커스텀 ISO용).</summary>
    IReadOnlyList<AppxPackageInfo> GetRemovalCatalog();

    Task RefreshInstalledStateAsync(IEnumerable<AppxPackageInfo> packages, CancellationToken ct = default);

    /// <summary>모든 사용자·프로비저닝 패키지에서 앱을 제거한다.</summary>
    Task RemoveAsync(AppxPackageInfo package, CancellationToken ct = default);

    /// <summary>Microsoft Store 에서 다시 설치할 수 있도록 스토어 페이지를 연다.</summary>
    Task OpenStoreAsync(AppxPackageInfo package);
}

public sealed class AppxService(IShellService shell) : IAppxService
{
    private readonly IShellService _shell = shell;

    /// <summary>
    /// 실제 한국어 Windows 11 에 설치/표시되는 정식 프로그램 및 기능 이름 카탈로그.
    /// </summary>
    private static readonly AppxPackageInfo[] Catalog =
    [
        // 3D & 미디어
        Entry("Microsoft.Microsoft3DViewer", "3D 뷰어", "3D 모델 뷰어 앱입니다.", true, "Microsoft.3DViewer"),
        Entry("Microsoft.Print3D", "Print 3D (3D 인쇄)", "3D 프린터용 앱입니다.", true),
        Entry("Microsoft.MixedReality.Portal", "Mixed Reality 포털", "VR/혼합 현실 포털입니다.", true, "MixedReality"),
        Entry("Clipchamp.Clipchamp", "Clipchamp - 동영상 편집기", "동영상 편집기 앱입니다.", true),
        Entry("Microsoft.Paint", "그림판", "Windows 기본 그림판 앱입니다.", false, "Microsoft.MSPaint"),
        Entry("Microsoft.MSPaint", "그림판 3D", "3D 그리기 도구입니다.", true, "Microsoft.Paint3D", "Microsoft.MSPaint3D"),
        Entry("Microsoft.Windows.Photos", "사진", "기본 사진 뷰어 앱입니다.", false),
        Entry("Microsoft.ZuneVideo", "영화 및 TV", "동영상 재생기 겸 스토어 프런트입니다.", false),
        Entry("Microsoft.ZuneMusic", "미디어 플레이어", "현대적인 기본 미디어 플레이어입니다.", false, "Microsoft.WindowsMediaPlayer"),
        Entry("MediaPlayback", "Windows Media Player (레거시)", "클래식 Windows Media Player 구성 요소입니다.", false, "WindowsMediaPlayer"),

        // 생산성 & 오피스
        Entry("Microsoft.WindowsCalculator", "계산기", "기본 계산기 앱입니다.", false),
        Entry("Microsoft.WindowsNotepad", "메모장", "현대적인 메모장 앱입니다.", false, "Microsoft.ModernNotepad"),
        Entry("Microsoft.MicrosoftStickyNotes", "스티커 메모", "Windows 스티커 메모 앱입니다.", false),
        Entry("Microsoft.Todos", "Microsoft To Do", "할 일 관리 앱입니다.", true),
        Entry("Microsoft.Office.OneNote", "OneNote", "디지털 전자 필기장 앱입니다.", false, "Microsoft.OneNote"),
        Entry("Microsoft.MicrosoftOfficeHub", "Microsoft 365 (Office)", "Microsoft 365 홍보 및 런처 앱입니다.", true),
        Entry("Microsoft.OutlookForWindows", "새 Outlook", "웹 기반 메일 클라이언트입니다.", true),
        Entry("microsoft.windowscommunicationsapps", "메일 및 캘린더", "구형 Windows 메일·캘린더 앱입니다.", false, "Microsoft.WindowsCommunicationsApps"),
        Entry("Microsoft.People", "사람", "구형 연락처 앱입니다.", true),
        Entry("Microsoft.OneDriveSync", "Microsoft OneDrive", "클라우드 스토리지 동기화 클라이언트입니다.", false, "Microsoft.OneDrive"),
        Entry("Microsoft.OneSync", "OneSync (동기화 호스트)", "계정 및 메일 동기화 구성 요소입니다.", false, "Microsoft.Exchange.OneSync"),

        // AI & 검색 & 정보
        Entry("Microsoft.Copilot", "Microsoft Copilot", "Windows Copilot AI 어시스턴트입니다.", true, "Microsoft.Windows.Ai.Copilot.Provider", "Microsoft.MicrosoftOfficeHub.Copilot"),
        Entry("Microsoft.Windows.Recall", "Windows Recall", "Windows 11 Recall AI 스냅샷 기능입니다.", true, "Microsoft.Recall", "Microsoft.Windows.Ai.Recall"),
        Entry("Microsoft.549981C3F5F10", "Cortana", "구형 Cortana 음성 비서 앱입니다.", true, "Microsoft.Cortana"),
        Entry("Microsoft.BingSearch", "웹 검색 (Bing)", "시작 메뉴의 웹 검색 결과 앱입니다.", true),
        Entry("Microsoft.BingNews", "뉴스 (MSN)", "위젯 패널의 뉴스 피드를 제공합니다.", true),
        Entry("Microsoft.BingWeather", "날씨 (MSN)", "작업 표시줄 위젯의 날씨 정보를 제공합니다.", true),
        Entry("Microsoft.BingFinance", "금융 (MSN)", "구형 Bing 금융 위젯 앱입니다.", true),
        Entry("Microsoft.BingSports", "스포츠 (MSN)", "구형 Bing 스포츠 위젯 앱입니다.", true),
        Entry("MicrosoftWindows.Client.WebExperience", "Windows 웹 환경 팩 (위젯)", "작업 표시줄 위젯 패널입니다. 제거 시 위젯이 완전히 비활성화됩니다.", false),
        Entry("Microsoft.WidgetsPlatformRuntime", "위젯 플랫폼 런타임", "위젯 패널 실행에 필요한 런타임입니다.", false),
        Entry("Microsoft.StartExperiencesApp", "시작 환경 (추천 앱)", "시작 메뉴 추천·프로모션 경험입니다.", true),

        // 커뮤니케이션 & 연결
        Entry("MicrosoftTeams", "Microsoft Teams (무료)", "채팅 아이콘과 연결된 개인용 Teams 입니다.", true, "Microsoft.Teams"),
        Entry("MSTeams", "Microsoft Teams (회사 또는 학교)", "새 업무용 Teams 클라이언트입니다.", false),
        Entry("Microsoft.SkypeApp", "Skype", "스카이프 통화 및 메시지 앱입니다.", true, "Skype"),
        Entry("Microsoft.YourPhone", "휴대폰과 연결", "스마트폰과 PC 를 연동하는 앱입니다.", false, "MicrosoftWindows.CrossDevice", "Microsoft.YourPhone.YourPhone"),
        Entry("Microsoft.Messaging", "메시징", "구형 SMS/메시징 앱입니다.", true),
        Entry("Microsoft.OneConnect", "모바일 요금제 (셀룰러)", "모바일 데이터·SIM 관련 앱입니다.", true),

        // 게임 & Xbox
        Entry("Microsoft.GamingApp", "Xbox", "Xbox Game Pass 및 게임 스토어 클라이언트입니다.", false, "Microsoft.XboxApp"),
        Entry("Microsoft.XboxGamingOverlay", "Xbox Game Bar", "Win + G 게임 녹화 및 오버레이입니다.", true),
        Entry("Microsoft.XboxGameOverlay", "Xbox Game Bar 플러그인", "Game Bar 보조 오버레이 구성 요소입니다.", true),
        Entry("Microsoft.XboxIdentityProvider", "Xbox Identity Provider", "Xbox 계정 로그인용 구성 요소입니다.", false),
        Entry("Microsoft.XboxSpeechToTextOverlay", "Xbox 음성 자막", "게임 음성 자막 오버레이입니다.", true),
        Entry("Microsoft.Xbox.TCUI", "Xbox TCUI", "Xbox 앱 UI 구성 요소입니다.", true),
        Entry("Microsoft.MicrosoftSolitaireCollection", "Microsoft Solitaire Collection", "기본 카드 게임 모음입니다.", true),
        Entry("Microsoft.MicrosoftEdge.GameAssist", "Edge Game Assist", "Edge 게임 보조 오버레이입니다.", true),

        // 유틸리티 & 시스템 도구
        Entry("Microsoft.ScreenSketch", "캡처 도구", "화면 캡처 및 녹화 도구입니다.", false),
        Entry("Microsoft.WindowsCamera", "카메라", "기본 웹캠·카메라 앱입니다.", false),
        Entry("Microsoft.WindowsAlarms", "시계", "알람, 타이머, 스톱워치 앱입니다.", false),
        Entry("Microsoft.WindowsSoundRecorder", "음성 녹음기", "기본 음성 녹음 앱입니다.", false),
        Entry("Microsoft.GetHelp", "도움말", "Microsoft 지원 문의 앱입니다.", true),
        Entry("Microsoft.Getstarted", "시작 (팁)", "Windows 사용 팁 및 시작 안내 앱입니다.", true, "Microsoft.Tips", "Microsoft.GetStarted"),
        Entry("Microsoft.WindowsFeedbackHub", "피드백 허브", "진단 데이터 수집 및 피드백 앱입니다.", true),
        Entry("MicrosoftCorporationII.QuickAssist", "빠른 지원", "원격 화면 공유 및 지원 도구입니다.", false, "Microsoft.QuickAssist"),
        Entry("MicrosoftCorporationII.MicrosoftFamily", "Microsoft Family Safety", "가족 계정 관리 및 자녀 보호 앱입니다.", true, "Microsoft.Family"),
        Entry("Microsoft.Wallet", "Microsoft 지갑", "결제 및 전자지갑 구성 요소입니다.", true, "Microsoft.Windows.Wallet"),
        Entry("Microsoft.WindowsMaps", "지도", "오프라인 지도 앱입니다.", true),
        Entry("Microsoft.Windows.DevHome", "개발자 홈 (Dev Home)", "개발자용 위젯 대시보드입니다.", true),
        Entry("Microsoft.PowerAutomateDesktop", "Power Automate", "업무 자동화 도구입니다.", true),
        Entry("MicrosoftCorporationII.Windows365", "Windows 365", "클라우드 PC 접속용 앱입니다.", true),
        Entry("Microsoft.RemoteDesktop", "원격 데스크톱", "원격 데스크톱 연결 클라이언트입니다.", false),
        Entry("Microsoft.WindowsTerminal", "터미널", "현대적인 터미널 환경입니다.", false),
        Entry("Microsoft.WindowsStore", "Microsoft Store", "스토어 앱입니다. (제거 시 재설치가 매우 어려우니 주의하십시오)", false),

        // 선택적 기능 및 시스템 구성 요소 (FOD / Optional Features)
        Entry("Microsoft-Windows-WordPad-Package", "워드패드", "클래식 워드패드 문서 편집기입니다.", true, "WordPad", "Microsoft.Windows.WordPad", "Microsoft-Windows-WordPad-Package"),
        Entry("Microsoft-Windows-StepsRecorder-Package", "단계 레코더", "문제 단계 기록 도구입니다.", true, "StepsRecorder", "App.StepsRecorder", "Microsoft.Windows.StepsRecorder", "Microsoft-Windows-StepsRecorder-Package"),
        Entry("Microsoft-Windows-TabletPCMath-Package", "수학 식 입력판", "수식 인식 패널 도구입니다.", true, "MathRecognizer", "Microsoft.Windows.MathRecognizer", "Microsoft-Windows-TabletPCMath-Package"),
        Entry("OpenSSH-Client-Package", "OpenSSH 클라이언트", "SSH 명령어 클라이언트 도구입니다.", false, "OpenSSH-Client", "OpenSSH.Client", "OpenSSH-Client-Package"),
        Entry("Microsoft-Windows-PowerShell-ISE-FOD-Package", "Windows PowerShell ISE", "PowerShell 스크립트 에디터입니다.", false, "Microsoft.Windows.PowerShell.ISE", "PowerShell.ISE", "PowerShellISE", "Microsoft.PowerShellISE", "Microsoft-Windows-PowerShell-ISE-FOD-Package"),
        Entry("Microsoft-Windows-PowerShell-V2-Client-Package", "Windows PowerShell 2.0", "구형 PowerShell 2.0 엔진입니다.", true, "MicrosoftWindows.PowerShell2", "Microsoft.Windows.PowerShell.V2", "MicrosoftWindowsPowerShellV2", "MicrosoftWindowsPowerShellV2Root", "Microsoft.Windows.PowerShellV2", "Microsoft-Windows-PowerShell-V2-Client-Package"),
        Entry("Microsoft-Windows-Hello-Face-Package", "Windows Hello 얼굴 인식", "얼굴 인식 로그인 구성 요소입니다.", false, "Hello.Face", "App.Face.BioEnrollment", "Windows.Hello.Face", "Microsoft-Windows-Hello-Face-Package"),
        Entry("Microsoft-Windows-InternetExplorer-Optional-Package", "Internet Explorer 모드", "레거시 IE 브라우저 구성 요소입니다.", true, "Browser.InternetExplorer", "Internet-Explorer-Optional-amd64", "Internet-Explorer-Optional-arm64", "Internet-Explorer-Optional-x86", "InternetExplorer", "Microsoft-Windows-InternetExplorer-Optional-Package")
    ];

    private static AppxPackageInfo Entry(
        string package, string display, string description, bool recommended, params string[] aliases) => new()
    {
        PackageName = package,
        DisplayName = display,
        Description = description,
        RecommendedForRemoval = recommended,
        Aliases = aliases
    };

    public async Task<IReadOnlyList<AppxPackageInfo>> LoadCatalogAsync(CancellationToken ct = default)
    {
        var list = GetRemovalCatalog().ToList();
        await RefreshInstalledStateAsync(list, ct).ConfigureAwait(false);
        return list;
    }

    public IReadOnlyList<AppxPackageInfo> GetRemovalCatalog()
        => Catalog
            .Select(c => new AppxPackageInfo
            {
                PackageName = c.PackageName,
                DisplayName = c.DisplayName,
                Description = c.Description,
                RecommendedForRemoval = c.RecommendedForRemoval,
                Aliases = c.Aliases,
                IsSelected = false
            })
            .ToList();

    public async Task RefreshInstalledStateAsync(IEnumerable<AppxPackageInfo> packages, CancellationToken ct = default)
    {
        var items = packages.ToList();
        if (items.Count == 0) return;

        var installed = await QueryInstalledPackageNamesAsync(ct).ConfigureAwait(false);

        foreach (var item in items)
        {
            item.InstalledPackageName = item.CandidateNames.FirstOrDefault(installed.Contains);
            item.IsInstalled = item.InstalledPackageName is not null;
            if (!item.IsInstalled)
                item.IsSelected = false;
        }
    }

    public async Task RemoveAsync(AppxPackageInfo package, CancellationToken ct = default)
    {
        var wanted = package.CandidateNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (wanted.Count == 0)
            return;

        var script = BuildOnlineRemovalScript(wanted);
        await _shell.RunPowerShellAsync(script, ct).ConfigureAwait(false);

        // Windows 비동기 패키지 정리 대기 및 재확인 (최대 5회)
        for (var retry = 0; retry < 5; retry++)
        {
            await RefreshInstalledStateAsync([package], ct).ConfigureAwait(false);
            if (!package.IsInstalled)
            {
                return;
            }
            await Task.Delay(300, ct).ConfigureAwait(false);
        }

        // 최종 확인 후에도 남아있을 때만 예외 발생
        await RefreshInstalledStateAsync([package], ct).ConfigureAwait(false);
        if (package.IsInstalled)
        {
            var detail = package.InstalledPackageName ?? package.PackageName;
            throw new TweakOperationException(
                $"{package.DisplayName} 제거 후에도 남아 있습니다: {detail}. Windows 가 보호하는 구성 요소이거나 다른 이름으로 재설치되었을 수 있습니다.");
        }
    }

    public Task OpenStoreAsync(AppxPackageInfo package)
        => _shell.OpenUrlAsync($"ms-windows-store://search/?query={Uri.EscapeDataString(package.DisplayName)}");

    private Task<HashSet<string>> QueryInstalledPackageNamesAsync(CancellationToken ct)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. 현재 사용자 패키지 저장소 (HKCU AppModel Repository)
        try
        {
            using var userPackagesKey = Registry.CurrentUser.OpenSubKey(
                @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            if (userPackagesKey is not null)
            {
                foreach (var pkgName in userPackagesKey.GetSubKeyNames())
                {
                    AddNameAndFamily(names, pkgName);
                }
            }
        }
        catch { }

        // 2. 모든 사용자 실제 설치 저장소 (HKLM AppxAllUserStore\User\{SID})
        try
        {
            using var allUserKey = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\User");
            if (allUserKey is not null)
            {
                foreach (var userSid in allUserKey.GetSubKeyNames())
                {
                    using var sidKey = allUserKey.OpenSubKey(userSid);
                    if (sidKey is null) continue;
                    foreach (var pkgName in sidKey.GetSubKeyNames())
                    {
                        AddNameAndFamily(names, pkgName);
                    }
                }
            }
        }
        catch { }

        // 3. CBS (Component Based Servicing) 선택적 기능(FOD / Capabilities) 실제 설치 상태 검사
        CheckCbsPackages(names);

        return Task.FromResult(names);
    }

    private static readonly HashSet<string> SystemProtectedPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.BioEnrollment",
        "Microsoft.Windows.ShellExperienceHost",
        "Microsoft.Windows.StartMenuExperienceHost",
        "Microsoft.Windows.CloudExperienceHost",
        "Microsoft.Windows.ContentDeliveryManager",
        "Microsoft.AAD.BrokerPlugin",
        "Microsoft.AccountsControl",
        "MicrosoftWindows.Client.CoreAI",
        "MicrosoftWindows.Client.CBS",
        "MicrosoftWindows.Client.Photon",
        "MicrosoftWindows.Client.FileExp",
        "MicrosoftWindows.Client.Core"
    };

    private static void AddNameAndFamily(HashSet<string> names, string packageFullName)
    {
        names.Add(packageFullName);
        var parts = packageFullName.Split('_', 2);
        if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]))
        {
            if (!SystemProtectedPackages.Contains(parts[0]))
            {
                names.Add(parts[0]);
            }
        }
    }

    private static void CheckCbsPackages(HashSet<string> names)
    {
        try
        {
            using var cbsKey = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\Packages");
            if (cbsKey is null) return;

            foreach (var pkgName in cbsKey.GetSubKeyNames())
            {
                using var subKey = cbsKey.OpenSubKey(pkgName);
                if (subKey is null) continue;

                var stateObj = subKey.GetValue("CurrentState");
                // 112 (0x70) = Installed, 96 (0x60) = InstallPending, 128 (0x80) = Permanent
                if (stateObj is int state && (state == 112 || state == 96 || state == 128))
                {
                    var baseName = pkgName.Split('~')[0];
                    names.Add(baseName);
                    names.Add(pkgName);
                }
            }
        }
        catch { }
    }

    private static string BuildOnlineRemovalScript(IReadOnlyList<string> wanted)
    {
        var wantedLiteral = string.Join(", ", wanted.Select(PsQuote));
        return $$"""
            $ErrorActionPreference = 'Continue'
            $wanted = @({{wantedLiteral}})

            function Test-Wanted([string]$Name) {
                if ([string]::IsNullOrWhiteSpace($Name)) { return $false }
                $family = ($Name -split '_', 2)[0]
                $cap = ($Name -split '~~~~', 2)[0]
                foreach ($w in $wanted) {
                    if ($Name -eq $w -or $family -eq $w -or $cap -eq $w) { return $true }
                    if ($Name.StartsWith($w + '_', [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
                    if ($Name.StartsWith($w + '~~~~', [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
                    if ($cap.Equals($w, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
                    if ($cap.EndsWith('.' + $w, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
                    if ($w.EndsWith('.' + $cap, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
                    if ($cap -like "*$w*" -or $w -like "*$cap*") { return $true }
                    if ($Name -like "*$w*") { return $true }
                }
                return $false
            }

            $pass = 0
            do {
                $pass++
                $removed = 0
                foreach ($c in @(Get-WindowsCapability -Online -ErrorAction SilentlyContinue | Where-Object { $_.State -eq 'Installed' -and (Test-Wanted $_.Name) })) {
                    try {
                        Remove-WindowsCapability -Online -Name $c.Name -ErrorAction Stop | Out-Null
                        Write-Output ('OK:capability:' + $c.Name)
                        $removed++
                    }
                    catch {
                        # DISM 직접 실행 fallback
                        dism.exe /Online /Remove-Capability /CapabilityName:$($c.Name) /NoRestart | Out-Null
                        if ($LASTEXITCODE -eq 0) {
                            Write-Output ('OK:capability-dism:' + $c.Name)
                            $removed++
                        } else {
                            Write-Output ('FAIL:capability:' + $c.Name + ':' + $_.Exception.Message)
                        }
                    }
                }
                foreach ($f in @(Get-WindowsOptionalFeature -Online -ErrorAction SilentlyContinue | Where-Object { $_.State -eq 'Enabled' -and (Test-Wanted $_.FeatureName) })) {
                    try {
                        Disable-WindowsOptionalFeature -Online -FeatureName $f.FeatureName -NoRestart -ErrorAction Stop | Out-Null
                        Write-Output ('OK:feature:' + $f.FeatureName)
                        $removed++
                    }
                    catch {
                        # DISM 직접 실행 fallback
                        dism.exe /Online /Disable-Feature /FeatureName:$($f.FeatureName) /NoRestart | Out-Null
                        if ($LASTEXITCODE -eq 0) {
                            Write-Output ('OK:feature-dism:' + $f.FeatureName)
                            $removed++
                        } else {
                            Write-Output ('FAIL:feature:' + $f.FeatureName + ':' + $_.Exception.Message)
                        }
                    }
                }
                foreach ($p in @(Get-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue | Where-Object { Test-Wanted $_.PackageName })) {
                    try {
                        Remove-AppxProvisionedPackage -Online -PackageName $p.PackageName -ErrorAction Stop
                        Write-Output ('OK:provisioned:' + $p.PackageName)
                        $removed++
                    }
                    catch {
                        Write-Output ('FAIL:provisioned:' + $p.PackageName + ':' + $_.Exception.Message)
                    }
                }
                foreach ($p in @(Get-AppxPackage -ErrorAction SilentlyContinue | Where-Object { Test-Wanted $_.Name })) {
                    try {
                        Remove-AppxPackage -Package $p.PackageFullName -ErrorAction Stop
                        Write-Output ('OK:package-user:' + $p.Name)
                        $removed++
                    }
                    catch {
                        Write-Output ('FAIL:package-user:' + $p.Name + ':' + $_.Exception.Message)
                    }
                }
                foreach ($p in @(Get-AppxPackage -AllUsers -ErrorAction SilentlyContinue | Where-Object { Test-Wanted $_.Name })) {
                    try {
                        Remove-AppxPackage -Package $p.PackageFullName -AllUsers -ErrorAction Stop
                        Write-Output ('OK:package-allusers:' + $p.Name)
                        $removed++
                    }
                    catch {
                        Write-Output ('FAIL:package-allusers:' + $p.Name + ':' + $_.Exception.Message)
                    }
                }
            } while ($removed -gt 0 -and $pass -lt 5)

            Get-AppxProvisionedPackage -Online | Where-Object { Test-Wanted $_.PackageName } |
              ForEach-Object { Write-Output ('LEFT:provisioned:' + $_.PackageName) }
            Get-AppxPackage -AllUsers | Where-Object { Test-Wanted $_.Name } |
              ForEach-Object { Write-Output ('LEFT:package:' + $_.Name) }
            Get-WindowsCapability -Online -ErrorAction SilentlyContinue | Where-Object { $_.State -eq 'Installed' -and (Test-Wanted $_.Name) } |
              ForEach-Object { Write-Output ('LEFT:capability:' + $_.Name) }
            Get-WindowsOptionalFeature -Online -ErrorAction SilentlyContinue | Where-Object { $_.State -eq 'Enabled' -and (Test-Wanted $_.FeatureName) } |
              ForEach-Object { Write-Output ('LEFT:feature:' + $_.FeatureName) }
            """;
    }

    private static List<string> ParseLeftovers(string stdout)
    {
        var left = new List<string>();
        foreach (var line in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("LEFT:", StringComparison.OrdinalIgnoreCase))
                left.Add(line[(line.IndexOf(':') + 1)..]);
        }
        return left;
    }

    private static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";
}
