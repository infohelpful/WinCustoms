using CommunityToolkit.Mvvm.ComponentModel;

namespace WinCustoms.Services;

/// <summary>제거 후보로 제시할 기본 제공 앱.</summary>
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

    /// <summary>실제로 설치되어 있는 것으로 확인된 패키지 이름. 제거할 때 이 이름을 쓴다.</summary>
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

    Task RefreshInstalledStateAsync(IEnumerable<AppxPackageInfo> packages, CancellationToken ct = default);

    /// <summary>현재 사용자 기준으로 앱을 제거한다.</summary>
    Task RemoveAsync(AppxPackageInfo package, CancellationToken ct = default);

    /// <summary>Microsoft Store 에서 다시 설치할 수 있도록 스토어 페이지를 연다.</summary>
    Task OpenStoreAsync(AppxPackageInfo package);
}

public sealed class AppxService(IShellService shell) : IAppxService
{
    private readonly IShellService _shell = shell;

    /// <summary>
    /// 제거 대상은 화이트리스트로만 관리한다.
    /// 와일드카드로 싹 지우는 방식은 프레임워크 패키지까지 날려 시스템을 망가뜨리기 쉽다.
    /// </summary>
    private static readonly AppxPackageInfo[] Catalog =
    [
        Entry("Microsoft.XboxGamingOverlay", "Xbox Game Bar", "Win + G 오버레이. 게임 녹화를 쓰지 않으면 제거해도 됩니다.", true),
        Entry("Microsoft.XboxGameOverlay", "Xbox Game Overlay", "Game Bar 보조 구성 요소입니다.", true),
        Entry("Microsoft.XboxIdentityProvider", "Xbox Identity Provider", "Xbox 계정 로그인용. PC 게임 일부가 이를 요구할 수 있습니다.", false),
        Entry("Microsoft.XboxSpeechToTextOverlay", "Xbox 음성 자막", "거의 쓰이지 않는 접근성 오버레이입니다.", true),
        Entry("Microsoft.MicrosoftSolitaireCollection", "Solitaire Collection", "광고가 포함된 기본 게임입니다.", true),
        Entry("Microsoft.BingNews", "뉴스", "위젯 패널의 뉴스 피드를 제공합니다.", true),
        Entry("Microsoft.BingWeather", "날씨", "작업 표시줄 위젯의 날씨 정보를 제공합니다.", true),
        Entry("Microsoft.BingSearch", "웹 검색(Bing)", "시작 메뉴의 웹 검색 결과 앱입니다.", true),
        Entry("MicrosoftTeams", "Teams (개인용)", "채팅 아이콘과 연결된 소비자용 Teams 입니다.", true),
        Entry("MSTeams", "Microsoft Teams", "새 Teams 클라이언트입니다. 업무용으로 쓴다면 남겨 두세요.", false),
        Entry("Microsoft.GetHelp", "도움말", "Microsoft 지원 문의 앱입니다.", true),
        Entry("Microsoft.Getstarted", "시작 도우미", "Windows 사용법 안내 앱입니다.", true),
        Entry("Microsoft.WindowsFeedbackHub", "피드백 허브", "진단 데이터 수집과 함께 동작합니다.", true),
        Entry("Microsoft.MicrosoftOfficeHub", "Office 허브", "Microsoft 365 홍보용 런처입니다.", true),
        Entry("Microsoft.People", "사람", "구형 연락처 앱입니다.", true),
        Entry("Microsoft.ZuneMusic", "미디어 플레이어", "기본 음악 재생기입니다. 다른 플레이어를 쓴다면 제거해도 됩니다.", false),
        Entry("Microsoft.ZuneVideo", "영화 및 TV", "동영상 재생기 겸 스토어 프런트입니다.", false),
        Entry("Microsoft.WindowsMaps", "지도", "오프라인 지도 앱입니다.", true),
        Entry("Clipchamp.Clipchamp", "Clipchamp", "구독형 동영상 편집기입니다.", true),
        Entry("Microsoft.Todos", "To Do", "할 일 관리 앱입니다.", true),
        Entry("Microsoft.OutlookForWindows", "새 Outlook", "웹 기반 메일 클라이언트입니다.", true),
        Entry("Microsoft.Windows.DevHome", "Dev Home", "개발자용 대시보드입니다.", true),
        Entry("Microsoft.Copilot", "Copilot", "Windows Copilot 앱입니다.", true,
            "Microsoft.Windows.Ai.Copilot.Provider", "Microsoft.MicrosoftOfficeHub.Copilot"),
        Entry("MicrosoftCorporationII.QuickAssist", "빠른 지원", "원격 지원 도구입니다. 남을 도와줄 일이 없다면 제거해도 됩니다.", false,
            "Microsoft.QuickAssist"),
        Entry("MicrosoftCorporationII.Windows365", "Windows 365", "클라우드 PC 접속용 앱입니다. 회사에서 쓰지 않는다면 불필요합니다.", true),
        Entry("Microsoft.PowerAutomateDesktop", "Power Automate", "업무 자동화 도구입니다. 쓰지 않으면 제거해도 됩니다.", true),
        Entry("MicrosoftWindows.CrossDevice", "휴대폰 연결(크로스 디바이스)", "휴대폰과 PC 를 연동합니다. 연동을 쓰지 않으면 제거해도 됩니다.", false),
        Entry("MicrosoftWindows.Client.WebExperience", "위젯(웹 환경)", "작업 표시줄 위젯 패널을 제공합니다. 제거하면 위젯 기능이 완전히 사라집니다.", false)
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
        // 매번 새 인스턴스를 만들어 페이지 간 선택 상태가 섞이지 않도록 한다.
        var list = Catalog
            .Select(c => new AppxPackageInfo
            {
                PackageName = c.PackageName,
                DisplayName = c.DisplayName,
                Description = c.Description,
                RecommendedForRemoval = c.RecommendedForRemoval,
                Aliases = c.Aliases
            })
            .ToList();

        await RefreshInstalledStateAsync(list, ct).ConfigureAwait(false);
        return list;
    }

    public async Task RefreshInstalledStateAsync(IEnumerable<AppxPackageInfo> packages, CancellationToken ct = default)
    {
        var items = packages.ToList();
        if (items.Count == 0) return;

        // 한 번의 PowerShell 호출로 설치된 패키지 이름만 받아온다(호출당 1초 이상 걸리므로 배치가 중요).
        const string script = """
            $ErrorActionPreference = 'SilentlyContinue'
            Get-AppxPackage | Select-Object -ExpandProperty Name
            """;

        var result = await _shell.RunPowerShellAsync(script, ct).ConfigureAwait(false);

        var installed = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            item.InstalledPackageName = item.CandidateNames.FirstOrDefault(installed.Contains);
            item.IsInstalled = item.InstalledPackageName is not null;
        }
    }

    public async Task RemoveAsync(AppxPackageInfo package, CancellationToken ct = default)
    {
        var name = (package.InstalledPackageName ?? package.PackageName).Replace("'", "''");

        // $$""" 는 보간 구분자를 '{{ }}' 로 올려서, PowerShell 의 단일 중괄호를 그대로 쓸 수 있게 한다.
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $pkg = Get-AppxPackage -Name '{{name}}'
            if ($null -eq $pkg) { exit 0 }
            $pkg | Remove-AppxPackage
            """;

        var result = await _shell.RunPowerShellAsync(script, ct).ConfigureAwait(false);

        if (!result.Succeeded)
            throw new TweakOperationException($"{package.DisplayName} 제거 실패: {result.Combined.Trim()}");

        package.IsInstalled = false;
    }

    public Task OpenStoreAsync(AppxPackageInfo package)
        => _shell.OpenUrlAsync($"ms-windows-store://search/?query={Uri.EscapeDataString(package.DisplayName)}");
}
