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

        _installedIdsCache ??= await ListInstalledIdsAsync(path, ct).ConfigureAwait(false);

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

        var args = new List<string>
        {
            "install",
            "--id", package.Id,
            "--exact",
            "--accept-package-agreements",
            "--accept-source-agreements",
            "--disable-interactivity"
        };

        if (!string.IsNullOrWhiteSpace(package.Source))
        {
            args.Add("--source");
            args.Add(package.Source);
        }

        var result = await _shell.RunAsync(path, args, ct).ConfigureAwait(false);

        if (result.Succeeded || LooksAlreadyInstalled(result))
        {
            package.IsInstalled = true;
            package.IsSelected = false;
            package.LastError = null;
            _installedIdsCache?.Add(package.Id);
            return;
        }

        var detail = string.IsNullOrWhiteSpace(result.Combined)
            ? $"종료 코드 {result.ExitCode}"
            : result.Combined.Trim();

        throw new InvalidOperationException(Truncate(detail, 400));
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
        var idStart = FindColumnStart(header, ["Id", "장치 ID", "패키지 ID"]);
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

            string? source = null;
            if (sourceStart >= 0)
            {
                var sourceEnd = matchStart > sourceStart ? matchStart : line.Length;
                source = SafeSlice(line, sourceStart, sourceEnd).Trim();
                if (string.IsNullOrWhiteSpace(source)) source = null;
            }

            var match = matchStart >= 0 ? SafeSlice(line, matchStart, line.Length).Trim() : string.Empty;

            if (string.IsNullOrWhiteSpace(id) || !LooksLikePackageId(id)) continue;
            if (!seen.Add(id)) continue;

            results.Add(new WingetPackageInfo
            {
                Id = id,
                DisplayName = string.IsNullOrWhiteSpace(name) ? id : name,
                Description = string.IsNullOrWhiteSpace(match) ? string.Empty : match,
                Category = source ?? "검색",
                Version = string.IsNullOrWhiteSpace(version) ? null : version,
                Source = source,
                Recommended = false
            });
        }

        return results;
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
               || result.ExitCode is -1978335189;
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
