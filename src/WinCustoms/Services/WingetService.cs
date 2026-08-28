using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WinCustoms.Services;

public sealed partial class WingetPackageInfo : ObservableObject
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required string Category { get; init; }

    /// <summary>버전 문자열. 검색 결과에서 채워지며, 추천 목록은 비워 둘 수 있다.</summary>
    public string? Version { get; init; }

    /// <summary>winget 소스 이름(winget / msstore). null 이면 설치 시 소스 지정 안 함.</summary>
    public string? Source { get; init; }

    /// <summary>목록에 보여줄 파일 소스 이름 (Winget / Microsoft Store / Chocolatey 등).</summary>
    public string SourceDisplay
    {
        get
        {
            var s = (Source ?? string.Empty).Trim();
            if (s.Length == 0 || s.Equals("winget", StringComparison.OrdinalIgnoreCase))
                return "Winget";
            if (s.Equals("msstore", StringComparison.OrdinalIgnoreCase))
                return "Microsoft Store";
            if (s.Equals("chocolatey", StringComparison.OrdinalIgnoreCase)
                || s.Equals("choco", StringComparison.OrdinalIgnoreCase))
                return "Chocolatey";
            if (s.Equals("winget-font", StringComparison.OrdinalIgnoreCase))
                return "Winget Font";
            return s;
        }
    }

    /// <summary>버전이 없을 때 목록용 표시.</summary>
    public string VersionDisplay
        => string.IsNullOrWhiteSpace(Version) ? "—" : Version!;

    /// <summary>처음 PC 세팅할 때 같이 깔아 두면 좋은 항목.</summary>
    public bool Recommended { get; init; }

    [ObservableProperty]
    public partial bool IsInstalled { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? LastError { get; set; }
}

public interface IWingetService
{
    /// <summary>winget 실행 파일이 있는지.</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>추천 패키지 목록과 설치 여부를 함께 가져온다.</summary>
    Task<IReadOnlyList<WingetPackageInfo>> LoadCatalogAsync(CancellationToken ct = default);

    /// <summary>winget 저장소에서 패키지를 검색한다.</summary>
    Task<IReadOnlyList<WingetPackageInfo>> SearchAsync(string query, CancellationToken ct = default);

    Task RefreshInstalledStateAsync(IEnumerable<WingetPackageInfo> packages, CancellationToken ct = default);

    /// <summary>선택한 패키지를 설치한다.</summary>
    Task InstallAsync(WingetPackageInfo package, CancellationToken ct = default);
}

/// <summary>
/// Windows Package Manager(winget) 로 프로그램을 검색·설치한다.
/// 추천 목록은 앱에 고정하고, 그 외는 <c>winget search</c> 결과를 그대로 보여 준다.
/// </summary>
public sealed class WingetService(IShellService shell) : IWingetService
{
    private const int SearchResultLimit = 40;

    private readonly IShellService _shell = shell;
    private string? _wingetPath;
    private HashSet<string>? _installedIdsCache;

    private static IReadOnlyList<WingetPackageInfo> Catalog { get; } =
    [
        // Windows 11 셸 · 작업 표시줄 (설치 직후 가장 많이 찾는 항목)
        Pkg("valinet.ExplorerPatcher", "ExplorerPatcher", "Windows 11 셸",
            "작업 표시줄·시작·탐색기를 Windows 10 느낌으로 되돌리는 패치. WinCustoms 레지스트리 트윅과 함께 쓰기 좋습니다.",
            recommended: true),
        Pkg("Open-Shell.Open-Shell-Menu", "Open-Shell", "Windows 11 셸",
            "클래식 시작 메뉴. 추천·광고 영역 없이 빠르게 프로그램을 실행합니다.",
            recommended: true),
        Pkg("StartIsBack.StartAllBack", "StartAllBack", "Windows 11 셸",
            "유료. 시작 메뉴·작업 표시줄·파일 탐색기를 세밀하게 예전 스타일로 복원합니다."),
        Pkg("RamenSoftware.Windhawk", "Windhawk", "Windows 11 셸",
            "작업 표시줄·시작·탐색기용 소형 모드(모드 마켓)를 설치해 세밀하게 꾸밉니다."),
        Pkg("File-New-Project.EarTrumpet", "EarTrumpet", "Windows 11 셸",
            "앱별 볼륨을 트레이에서 바로 조절합니다.", recommended: true),
        Pkg("CharlesMilette.TranslucentTB", "TranslucentTB", "Windows 11 셸",
            "작업 표시줄을 투명·반투명으로 만듭니다."),

        // 런타임 · 기반 (많은 프로그램이 이걸 필요로 함)
        Pkg("Microsoft.VCRedist.2015+.x64", "Visual C++ 재배포 패키지 (x64)", "런타임",
            "대부분의 데스크톱 앱이 필요로 하는 VC++ 런타임.", recommended: true),
        Pkg("Microsoft.VCRedist.2015+.x86", "Visual C++ 재배포 패키지 (x86)", "런타임",
            "32비트 앱용 VC++ 런타임. x64와 같이 깔아 두면 안전합니다.", recommended: true),
        Pkg("Microsoft.DotNet.DesktopRuntime.8", ".NET 8 Desktop Runtime", "런타임",
            "최신 .NET 데스크톱 앱 실행에 필요합니다."),
        Pkg("Microsoft.EdgeWebView2Runtime", "WebView2 Runtime", "런타임",
            "많은 앱의 내장 웹 UI에 쓰입니다. 보통 이미 있지만 없으면 설치하세요."),
        Pkg("Microsoft.DirectX", "DirectX End-User Runtime", "런타임",
            "오래된 게임·유틸이 요구하는 DirectX 구성 요소."),

        // 필수 유틸
        Pkg("7zip.7zip", "7-Zip", "필수 유틸",
            "ZIP·7z·RAR 등 압축 해제. Windows 기본 압축보다 훨씬 편합니다.", recommended: true),
        Pkg("voidtools.Everything", "Everything", "필수 유틸",
            "파일 이름을 즉시 검색. 새 PC에서 가장 먼저 깔아 두면 좋은 도구입니다.", recommended: true),
        Pkg("Microsoft.PowerToys", "PowerToys", "필수 유틸",
            "창 배치·빠른 실행·파일 이름 바꾸기 등 Windows 확장 도구 모음.", recommended: true),
        Pkg("ShareX.ShareX", "ShareX", "필수 유틸",
            "화면 캡처·녹화·업로드를 한곳에서. Win+Shift+S 보다 기능이 많습니다.", recommended: true),
        Pkg("Notepad++.Notepad++", "Notepad++", "필수 유틸",
            "가벼운 텍스트·코드 편집기.", recommended: true),
        Pkg("Microsoft.WindowsTerminal", "Windows Terminal", "필수 유틸",
            "탭·프로필을 지원하는 최신 터미널."),
        Pkg("AntibodySoftware.WizTree", "WizTree", "필수 유틸",
            "디스크 용량을 순식간에 분석해 큰 폴더를 찾습니다."),
        Pkg("LocalSend.LocalSend", "LocalSend", "필수 유틸",
            "같은 Wi-Fi 안 기기끼리 파일 보내기(에어드롭 느낌)."),
        Pkg("Adobe.Acrobat.Reader.64-bit", "Adobe Acrobat Reader", "필수 유틸",
            "PDF 보기. 업무·서류용으로 자주 필요합니다."),

        // 브라우저 · 미디어
        Pkg("Google.Chrome", "Google Chrome", "브라우저 · 미디어",
            "가장 널리 쓰이는 웹 브라우저.", recommended: true),
        Pkg("Mozilla.Firefox", "Mozilla Firefox", "브라우저 · 미디어",
            "개인정보 보호에 강한 오픈소스 브라우저."),
        Pkg("Brave.Brave", "Brave", "브라우저 · 미디어",
            "광고 차단이 기본으로 들어간 브라우저."),
        Pkg("VideoLAN.VLC", "VLC media player", "브라우저 · 미디어",
            "거의 모든 영상·음원을 재생. 코덱 걱정이 없습니다.", recommended: true),

        // 하드웨어 점검 (새 PC / 포맷 직후)
        Pkg("CrystalDewWorld.CrystalDiskInfo", "CrystalDiskInfo", "하드웨어",
            "SSD·HDD 상태(SMART) 확인. 새 저장 장치 점검에 유용합니다."),
        Pkg("CPUID.CPU-Z", "CPU-Z", "하드웨어",
            "CPU·메인보드·메모리 정보 확인."),
        Pkg("TechPowerUp.GPU-Z", "GPU-Z", "하드웨어",
            "그래픽 카드 정보 확인."),

        // 개발 · 계정 (자주 바로 까는 항목)
        Pkg("Git.Git", "Git", "개발 · 계정",
            "버전 관리 도구."),
        Pkg("Microsoft.VisualStudioCode", "Visual Studio Code", "개발 · 계정",
            "Microsoft 코드 에디터."),
        Pkg("Bitwarden.Bitwarden", "Bitwarden", "개발 · 계정",
            "오픈소스 비밀번호 관리자. 새 PC에서 로그인 정보를 바로 씁니다."),
        Pkg("Discord.Discord", "Discord", "개발 · 계정",
            "음성·채팅·커뮤니티 앱.")
    ];

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        var path = await ResolveWingetAsync(ct).ConfigureAwait(false);
        return path is not null;
    }

    public async Task<IReadOnlyList<WingetPackageInfo>> LoadCatalogAsync(CancellationToken ct = default)
    {
        _installedIdsCache = null;

        var packages = Catalog
            .Select(p => new WingetPackageInfo
            {
                Id = p.Id,
                DisplayName = p.DisplayName,
                Description = p.Description,
                Category = p.Category,
                Recommended = p.Recommended,
                Source = "winget"
            })
            .ToList();

        await RefreshInstalledStateAsync(packages, ct).ConfigureAwait(false);
        return packages;
    }

    public async Task<IReadOnlyList<WingetPackageInfo>> SearchAsync(string query, CancellationToken ct = default)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0)
            return [];

        var path = await ResolveWingetAsync(ct).ConfigureAwait(false)
                   ?? throw new InvalidOperationException(
                       "winget 을 찾을 수 없습니다. Microsoft Store 에서 '앱 설치 관리자(App Installer)'를 설치하거나 업데이트하세요.");

        var result = await _shell.RunAsync(path,
        [
            "search",
            trimmed,
            "--disable-interactivity",
            "--accept-source-agreements",
            "--count", SearchResultLimit.ToString()
        ], ct).ConfigureAwait(false);

        // 결과 없음(exit 코드)이어도 stdout 에 안내만 있을 수 있다.
        var packages = ParseSearchTable(result.Combined);
        if (packages.Count == 0 && !result.Succeeded
            && !result.Combined.Contains("No package", StringComparison.OrdinalIgnoreCase)
            && !result.Combined.Contains("패키지를 찾지", StringComparison.OrdinalIgnoreCase)
            && !result.Combined.Contains("일치하는 패키지", StringComparison.OrdinalIgnoreCase))
        {
            var detail = string.IsNullOrWhiteSpace(result.Combined)
                ? $"종료 코드 {result.ExitCode}"
                : result.Combined.Trim();
            throw new InvalidOperationException(Truncate(detail, 400));
        }

        await RefreshInstalledStateAsync(packages, ct).ConfigureAwait(false);
        return packages;
    }

    public async Task RefreshInstalledStateAsync(IEnumerable<WingetPackageInfo> packages, CancellationToken ct = default)
    {
        var path = await ResolveWingetAsync(ct).ConfigureAwait(false);
        if (path is null)
        {
            foreach (var package in packages)
                package.IsInstalled = false;
            return;
        }

        // 설치 직후 캐시가 낡을 수 있어 항상 다시 조회한다.
        _installedIdsCache = await ListInstalledIdsAsync(path, ct).ConfigureAwait(false);

        foreach (var package in packages)
        {
            ct.ThrowIfCancellationRequested();
            package.IsInstalled = _installedIdsCache.Contains(package.Id);
        }
    }

    public async Task InstallAsync(WingetPackageInfo package, CancellationToken ct = default)
    {
        var path = await ResolveWingetAsync(ct).ConfigureAwait(false)
                   ?? throw new InvalidOperationException(
                       "winget 을 찾을 수 없습니다. Microsoft Store 에서 '앱 설치 관리자(App Installer)'를 설치하거나 업데이트하세요.");

        if (string.IsNullOrWhiteSpace(package.Id) || !LooksLikePackageId(package.Id))
            throw new InvalidOperationException("패키지 ID가 올바르지 않습니다: " + package.Id);

        var args = new List<string>
        {
            "install",
            "--id", package.Id.Trim(),
            "--exact",
            "--accept-package-agreements",
            "--accept-source-agreements",
            "--disable-interactivity"
        };

        // 검색 표 파싱이 Match 열(ProductCode: 7-zip)과 Source(winget)를 붙이면
        // --source "7-zip winget" 이 되어 설치가 실패한다. 유효한 소스만 넘긴다.
        var source = NormalizeWingetSource(package.Source);
        if (source is not null)
        {
            args.Add("--source");
            args.Add(source);
        }

        var result = await _shell.RunAsync(path, args, ct).ConfigureAwait(false);

        // winget 은 설치가 끝났어도 재시작 필요·경고 등으로 0이 아닌 코드를 줄 수 있다.
        // 출력 문구와 실제 list 조회로 성공을 판정한다.
        if (result.Succeeded
            || LooksAlreadyInstalled(result)
            || LooksInstallSucceeded(result)
            || await IsPackagePresentAsync(path, package.Id, ct).ConfigureAwait(false))
        {
            MarkInstalled(package);
            return;
        }

        var detail = string.IsNullOrWhiteSpace(result.Combined)
            ? $"종료 코드 {result.ExitCode}"
            : SummarizeWingetFailure(result.Combined, result.ExitCode);

        throw new InvalidOperationException(Truncate(detail, 400));
    }

    /// <summary>
    /// winget 전체 로그(Found/라이선스/Downloading…)를 그대로 오류로 보여 주지 않고,
    /// 실제 실패 원인 줄만 골라낸다.
    /// </summary>
    private static string SummarizeWingetFailure(string combined, int exitCode)
    {
        var lines = combined
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static l => l.Length > 0)
            .Where(static l => !IsWingetNoiseLine(l))
            .ToList();

        if (lines.Count > 0)
            return string.Join(Environment.NewLine, lines.TakeLast(4));

        // 진행 로그만 있고 실패 문구가 없으면(중간에 끊김 등)
        if (combined.Contains("Downloading", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("다운로드", StringComparison.OrdinalIgnoreCase))
        {
            return "다운로드/설치가 끝나기 전에 중단된 것 같습니다. 네트워크·백신 차단을 확인한 뒤 다시 시도하세요."
                   + $" (코드 {exitCode})";
        }

        return $"설치에 실패했습니다. (코드 {exitCode})";
    }

    private static bool IsWingetNoiseLine(string line)
    {
        if (line.StartsWith("Found ", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.StartsWith("This application is licensed", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.StartsWith("Microsoft is not responsible", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.StartsWith("Downloading ", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.StartsWith("Downloading:", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.Contains("████", StringComparison.Ordinal)) return true; // 진행 바
        if (line.StartsWith("  ████", StringComparison.Ordinal)) return true;
        if (line.Equals("…", StringComparison.Ordinal)) return true;
        return false;
    }

    private void MarkInstalled(WingetPackageInfo package)
    {
        package.IsInstalled = true;
        package.IsSelected = false;
        package.LastError = null;
        _installedIdsCache?.Add(package.Id);
    }

    private async Task<bool> IsPackagePresentAsync(string wingetPath, string id, CancellationToken ct)
    {
        try
        {
            var result = await _shell.RunAsync(wingetPath,
            [
                "list",
                "--id", id,
                "--exact",
                "--disable-interactivity"
            ], ct).ConfigureAwait(false);

            var output = result.Combined;
            if (string.IsNullOrWhiteSpace(output)) return false;

            // 미설치 시 영/한 메시지
            if (output.Contains("No installed package found", StringComparison.OrdinalIgnoreCase)
                || output.Contains("설치된 패키지를 찾을 수 없습니다", StringComparison.OrdinalIgnoreCase))
                return false;

            foreach (var raw in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (raw.Contains(id, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            // list 실패 시 설치 판정에 쓰지 않음
        }

        return false;
    }

    /// <summary>
    /// winget search 표 출력을 파싱한다.
    /// 헤더(영문/한글)의 Id·Version·Source 열 위치를 읽고 각 행을 잘라 낸다.
    /// </summary>
    internal static IReadOnlyList<WingetPackageInfo> ParseSearchTable(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return [];

        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var headerIndex = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (ContainsIdHeader(line) && (line.Contains("Name", StringComparison.OrdinalIgnoreCase)
                                           || line.Contains("이름", StringComparison.Ordinal)))
            {
                headerIndex = i;
                break;
            }
        }

        if (headerIndex < 0) return [];

        var header = lines[headerIndex];
        // "장치 ID" / "패키지 ID" 를 "Id" 보다 먼저 찾아야 한다.
        // IndexOf("Id") 는 "장치 ID" 의 "ID" 에 걸려 열이 3칸 밀리고,
        // 이름이 "7-Zip 7" · ID 가 "zip.7zip" 처럼 잘린다.
        var idStart = FindColumnStart(header, ["장치 ID", "패키지 ID", "Id"]);
        var versionStart = FindColumnStart(header, ["Version", "버전"]);
        var sourceStart = FindColumnStart(header, ["Source", "원본", "소스"]);
        var matchStart = FindColumnStart(header, ["Match", "일치"]);

        // Id 열을 못 찾으면 휴리스틱으로 되돌린다.
        if (idStart < 0)
            return ParseSearchHeuristic(lines, headerIndex);

        var versionEnd = NextColumnStart(versionStart, sourceStart, matchStart, header.Length);
        var idEnd = versionStart >= 0 ? versionStart : NextColumnStart(idStart, sourceStart, matchStart, header.Length);

        var results = new List<WingetPackageInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = headerIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0) continue;
            if (line.StartsWith('-') || line.StartsWith('\u2500') || line.StartsWith('<')) continue;
            if (line.Contains("결과 한도", StringComparison.Ordinal) || line.Contains("truncated", StringComparison.OrdinalIgnoreCase))
                continue;

            var name = SafeSlice(line, 0, idStart).Trim();
            var id = SafeSlice(line, idStart, idEnd).Trim();
            var version = versionStart >= 0 ? SafeSlice(line, versionStart, versionEnd).Trim() : string.Empty;

            // 긴 이름이 열을 침범하면 ID 가 깨진다 → 줄에서 패키지 ID 를 다시 찾는다.
            if (!LooksLikePackageId(id))
            {
                if (!TryExtractPackageId(line, out id, out var nameHint, out var versionHint))
                    continue;
                if (string.IsNullOrWhiteSpace(name) || name.EndsWith(id, StringComparison.OrdinalIgnoreCase))
                    name = nameHint;
                if (string.IsNullOrWhiteSpace(version))
                    version = versionHint;
            }

            string? source = null;
            if (sourceStart >= 0)
            {
                var sourceEnd = matchStart > sourceStart ? matchStart : line.Length;
                source = SafeSlice(line, sourceStart, sourceEnd).Trim();
            }

            // Match 열이 길면 Source 열과 붙는다(예: "ProductCode: 7-zip winget").
            // 줄 끝 토큰·알려진 소스명으로 바로잡는다.
            source = NormalizeWingetSource(source)
                     ?? NormalizeWingetSource(ExtractTrailingSourceToken(line));

            if (string.IsNullOrWhiteSpace(id) || !LooksLikePackageId(id)) continue;
            if (!seen.Add(id)) continue;

            results.Add(new WingetPackageInfo
            {
                Id = id,
                DisplayName = string.IsNullOrWhiteSpace(name) ? id : name,
                // Match 열(ProductCode/Tag/Moniker)은 UI 설명으로 쓰지 않는다.
                Description = string.Empty,
                Category = source ?? "검색",
                Version = string.IsNullOrWhiteSpace(version) ? null : version,
                Source = source,
                Recommended = false
            });
        }

        // 열 파싱이 전부 실패하면 휴리스틱으로 한 번 더.
        if (results.Count == 0)
            return ParseSearchHeuristic(lines, headerIndex);

        return results;
    }

    private static readonly Regex PackageIdInLine = new(
        @"(?<id>[A-Za-z0-9][A-Za-z0-9._+-]*\.[A-Za-z0-9][A-Za-z0-9._+-]*)",
        RegexOptions.Compiled);

    /// <summary>표 열이 깨졌을 때 줄에서 Publisher.Package 형태 ID 를 꺼낸다.</summary>
    private static bool TryExtractPackageId(string line, out string id, out string name, out string version)
    {
        id = string.Empty;
        name = string.Empty;
        version = string.Empty;

        var m = PackageIdInLine.Match(line);
        if (!m.Success) return false;

        id = m.Groups["id"].Value;
        if (!LooksLikePackageId(id)) return false;

        name = line[..m.Index].Trim();
        var after = line[(m.Index + m.Length)..].Trim();
        if (after.Length > 0)
        {
            var token = after.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries)[0];
            // 버전처럼 보이는 토큰만 (소스명 winget 등은 제외)
            if (!IsKnownWingetSource(token) && !token.Contains(':', StringComparison.Ordinal))
                version = token;
        }

        return true;
    }

    private static IReadOnlyList<WingetPackageInfo> ParseSearchHeuristic(string[] lines, int headerIndex)
    {
        // Name ... Id.With.Dots ... Version
        var rowPattern = new Regex(
            @"^(?<name>.+?)\s+(?<id>[A-Za-z0-9][A-Za-z0-9._+-]*\.[A-Za-z0-9][A-Za-z0-9._+-]*)\s+(?<version>\S+)",
            RegexOptions.Compiled);

        var results = new List<WingetPackageInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = headerIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith('-') || line.StartsWith('\u2500') || line.StartsWith('<')) continue;

            var m = rowPattern.Match(line);
            if (!m.Success) continue;

            var id = m.Groups["id"].Value;
            if (!seen.Add(id)) continue;

            results.Add(new WingetPackageInfo
            {
                Id = id,
                DisplayName = m.Groups["name"].Value.Trim(),
                Description = string.Empty,
                Category = "검색",
                Version = m.Groups["version"].Value,
                Recommended = false
            });
        }

        return results;
    }

    private static bool ContainsIdHeader(string line)
        => line.Contains("Id", StringComparison.OrdinalIgnoreCase)
           || line.Contains("장치 ID", StringComparison.Ordinal)
           || line.Contains("패키지 ID", StringComparison.Ordinal);

    private static int FindColumnStart(string header, IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            var idx = header.IndexOf(name, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) return idx;
        }

        return -1;
    }

    private static int NextColumnStart(int current, params int[] candidates)
    {
        var next = int.MaxValue;
        foreach (var c in candidates)
        {
            if (c > current && c < next) next = c;
        }

        return next == int.MaxValue ? int.MaxValue : next;
    }

    private static string SafeSlice(string line, int start, int end)
    {
        if (start < 0 || start >= line.Length) return string.Empty;
        if (end < 0 || end == int.MaxValue) end = line.Length;
        if (end > line.Length) end = line.Length;
        if (end <= start) return string.Empty;
        return line[start..end];
    }

    private static bool LooksLikePackageId(string id)
        => id.Contains('.')
           && !id.Contains(' ')
           && !id.Contains('\\')
           && id.Length is >= 3 and < 120
           && !id.StartsWith("http", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// winget --source 에 넣을 수 있는 값만 남긴다.
    /// 검색 표에서 Match+Source 가 붙으면 "7-zip winget" 같은 쓰레기가 생긴다.
    /// </summary>
    private static string? NormalizeWingetSource(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var text = raw.Trim();
        if (IsKnownWingetSource(text))
            return text;

        // "ProductCode: 7-zip winget" / "Tag: 7-zip         winget" → 마지막 토큰
        var parts = text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2 && IsKnownWingetSource(parts[^1]))
            return parts[^1];

        // 알 수 없는 소스라도 공백·콜론 없는 식별자면 허용(사용자 커스텀 소스)
        if (!text.Contains(' ')
            && !text.Contains(':')
            && text.Length is >= 2 and < 40
            && text.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            return text;

        return null;
    }

    private static bool IsKnownWingetSource(string name)
        => name.Equals("winget", StringComparison.OrdinalIgnoreCase)
           || name.Equals("msstore", StringComparison.OrdinalIgnoreCase)
           || name.Equals("winget-font", StringComparison.OrdinalIgnoreCase);

    private static string? ExtractTrailingSourceToken(string line)
    {
        var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? null : parts[^1];
    }

    private async Task<HashSet<string>> ListInstalledIdsAsync(string wingetPath, CancellationToken ct)
    {
        var result = await _shell.RunAsync(wingetPath,
        [
            "list",
            "--disable-interactivity"
        ], ct).ConfigureAwait(false);

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(result.StandardOutput)) return ids;

        foreach (var raw in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.StartsWith("Name", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("이름", StringComparison.Ordinal)
                || raw.StartsWith('-')
                || raw.StartsWith('\u2500'))
                continue;

            var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (LooksLikePackageId(part))
                    ids.Add(part);
            }
        }

        return ids;
    }

    private async Task<string?> ResolveWingetAsync(CancellationToken ct)
    {
        if (_wingetPath is not null) return _wingetPath;

        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "winget.exe");

        foreach (var candidate in new[] { local, "winget.exe", "winget" })
        {
            try
            {
                var result = await _shell.RunAsync(candidate, ["--version"], ct).ConfigureAwait(false);
                if (result.Succeeded || result.StandardOutput.Contains('v', StringComparison.OrdinalIgnoreCase))
                {
                    _wingetPath = candidate;
                    return _wingetPath;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or FileNotFoundException)
            {
                // 다음 후보로.
            }
        }

        return null;
    }

    private static bool LooksAlreadyInstalled(ProcessResult result)
    {
        var text = result.Combined;
        return text.Contains("already installed", StringComparison.OrdinalIgnoreCase)
               || text.Contains("이미 설치", StringComparison.OrdinalIgnoreCase)
               || result.ExitCode is -1978335189; // APPINSTALLER_CLI_ERROR_PACKAGE_ALREADY_INSTALLED
    }

    private static bool LooksInstallSucceeded(ProcessResult result)
    {
        var text = result.Combined;
        if (string.IsNullOrWhiteSpace(text)) return false;

        // 재시작 필요(설치는 끝난 상태)
        if (result.ExitCode is -1978334975 or -1978334967 or -1978334964)
            return true;

        return text.Contains("Successfully installed", StringComparison.OrdinalIgnoreCase)
               || text.Contains("successfully installed", StringComparison.OrdinalIgnoreCase)
               || text.Contains("Installation successful", StringComparison.OrdinalIgnoreCase)
               || text.Contains("설치했습니다", StringComparison.OrdinalIgnoreCase)
               || text.Contains("설치를 완료", StringComparison.OrdinalIgnoreCase)
               || text.Contains("성공적으로 설치", StringComparison.OrdinalIgnoreCase)
               || text.Contains("설치가 완료", StringComparison.OrdinalIgnoreCase);
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    private static WingetPackageInfo Pkg(
        string id, string name, string category, string description, bool recommended = false)
        => new()
        {
            Id = id,
            DisplayName = name,
            Category = category,
            Description = description,
            Recommended = recommended,
            Source = "winget"
        };
}
