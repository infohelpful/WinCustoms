#Requires -Version 5.1
<#
.SYNOPSIS
    WinCustoms 를 빌드해서 GitHub Releases 에 올린다. 버전은 자동으로 올라간다.

.DESCRIPTION
    버전 결정 → 게시 → 검증 → 실행 확인 → zip → 버전 커밋/푸시 → 릴리스 생성 순으로 진행한다.
    중간에 하나라도 실패하면 csproj 를 원래대로 되돌리고 멈춘다. 반쯤 올라간 상태는 남지 않는다.

    버전 규칙:
      - csproj 의 현재 버전이 아직 릴리스되지 않았으면 그 버전을 그대로 쓴다.
      - 이미 릴리스된 버전이면 끝자리를 1 올린다 (1.0.0 → 1.0.1).
      - -Minor / -Major / -Version 을 주면 그 지시를 따른다.

.PARAMETER Minor
    끝자리 대신 가운데 자리를 올린다 (1.0.3 → 1.1.0). 기능을 추가했을 때 쓴다.

.PARAMETER Major
    맨 앞자리를 올린다 (1.4.2 → 2.0.0). 크게 갈아엎었을 때 쓴다.

.PARAMETER Version
    버전을 직접 지정한다. 예: -Version 2.0.0

.PARAMETER Draft
    공개하지 않고 초안으로 만든다. 내용을 확인한 뒤 웹에서 [Publish] 를 눌러 게시한다.

.PARAMETER SkipUpload
    빌드와 zip 까지만 한다. 버전도 올리지 않고 커밋도 하지 않는다.

.PARAMETER SkipSmokeTest
    게시된 exe 를 실제로 띄워 보는 검증을 건너뛴다.

.PARAMETER ShowVersion
    다음 릴리스가 몇 번이 될지만 알려 주고 끝낸다. 아무것도 바꾸지 않는다.

.EXAMPLE
    .\scripts\release.ps1
    버그를 고쳤을 때. 1.0.0 이 이미 나가 있으면 1.0.1 로 올려서 배포한다.

.EXAMPLE
    .\scripts\release.ps1 -Minor
    기능을 추가했을 때. 1.0.3 → 1.1.0

.EXAMPLE
    .\scripts\release.ps1 -SkipUpload
    결과물만 확인. 아무것도 올리지 않고 버전도 그대로 둔다.
#>
[CmdletBinding()]
param(
    [switch]$Minor,
    [switch]$Major,
    [string]$Version,
    [switch]$Draft,
    [switch]$SkipUpload,
    [switch]$SkipSmokeTest,
    [switch]$ShowVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Windows PowerShell 5.1 은 콘솔 출력에 시스템 ANSI 코드 페이지(한국어라면 949)를 쓴다.
# 이 파일은 UTF-8 이라 그대로 두면 진행 메시지가 전부 깨져서 나온다.
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$Root       = Split-Path -Parent $PSScriptRoot
$Project    = Join-Path $Root 'src\WinCustoms\WinCustoms.csproj'
$PublishDir = Join-Path $Root 'publish'
$DistDir    = Join-Path $Root 'dist'

# 실패했을 때 csproj 를 되돌리기 위한 원본. 커밋에 성공하면 비운다.
$script:CsprojOriginal = $null

function Step([string]$text) { Write-Host ''; Write-Host "==> $text" -ForegroundColor Cyan }
function Note([string]$text) { Write-Host "    $text" -ForegroundColor DarkGray }

<#
    외부 명령을 출력 없이 실행하고 종료 코드만 돌려준다.
    ErrorActionPreference 가 Stop 이면 네이티브 명령이 stderr 에 한 줄만 써도
    PowerShell 이 NativeCommandError 로 스크립트를 끝내 버린다.
    "없으면 없다고 알려 주는" 조회성 명령에는 그 동작이 곤란하므로 잠시 꺼 둔다.
#>
function RunQuiet([string]$exe, [string[]]$cmdArgs) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $exe @cmdArgs 2>&1 | Out-Null
        return $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previous }
}

<#
    해당 버전이 이미 세상에 나갔는지 본다.
    보통은 태그로 확인되지만, 초안(draft) 릴리스는 게시하기 전까지 태그를 만들지 않는다.
    초안을 만들어 둔 버전을 또 쓰면 나중에 태그가 충돌하므로 릴리스 목록도 함께 확인한다.
#>
function TagExists([string]$tag) {
    if ((RunQuiet 'git' @('-C', $Root, 'rev-parse', '--verify', '--quiet', "refs/tags/$tag")) -eq 0) {
        return $true
    }

    return (RunQuiet 'gh' @('release', 'view', $tag)) -eq 0
}

function Fail([string]$text) {
    if ($script:CsprojOriginal) {
        [System.IO.File]::WriteAllText($Project, $script:CsprojOriginal, (New-Object System.Text.UTF8Encoding $false))
        Write-Host ''; Write-Host '    csproj 버전을 원래대로 되돌렸습니다.' -ForegroundColor DarkGray
    }

    Write-Host ''; Write-Host "!!  $text" -ForegroundColor Red
    Pop-Location -ErrorAction SilentlyContinue
    exit 1
}

# gh 는 현재 디렉터리로 대상 저장소를 판단하므로 저장소 루트에서 실행한다.
Push-Location $Root

# ── 1. 사전 점검 ──────────────────────────────────────────────
Step '사전 점검'

$csprojText = [System.IO.File]::ReadAllText($Project)
$match = [regex]::Match($csprojText, '<Version>\s*(\d+)\.(\d+)\.(\d+)\s*</Version>')
if (-not $match.Success) {
    Fail 'csproj 에서 <Version>x.y.z</Version> 을 찾지 못했습니다.'
}

$current = '{0}.{1}.{2}' -f $match.Groups[1].Value, $match.Groups[2].Value, $match.Groups[3].Value
$reason  = ''
Note "csproj 의 현재 버전 $current"

if ($SkipUpload) {
    # 올리지 않을 거면 버전도 건드리지 않는다.
    $newVersion = $current
}
else {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        Fail 'GitHub CLI(gh) 를 찾을 수 없습니다. winget install GitHub.cli 로 설치하세요.'
    }

    # 미리보기는 아무것도 바꾸지 않으므로 작업 트리가 지저분해도 상관없다.
    if (-not $ShowVersion) {
        $dirty = git -C $Root status --porcelain
        if ($dirty) {
            Fail ("커밋되지 않은 변경이 있습니다. 먼저 커밋하세요.`n" + ($dirty -join "`n"))
        }
    }

    # 태그는 로컬이 아니라 GitHub 쪽에 생긴다. 어떤 버전이 이미 나갔는지 알려면
    # 원격에서 태그를 받아와야 한다.
    git -C $Root fetch origin --tags --quiet
    if ($LASTEXITCODE -ne 0) { Fail 'origin 에서 fetch 하지 못했습니다. 네트워크와 원격 설정을 확인하세요.' }

    # ── 2. 버전 결정 ──────────────────────────────────────────
    # PowerShell 변수는 대소문자를 구분하지 않는다.
    # $major 로 두면 -Major 스위치 파라미터를 덮어써서 형 변환 오류가 난다.
    $curMajor = [int]$match.Groups[1].Value
    $curMinor = [int]$match.Groups[2].Value
    $curPatch = [int]$match.Groups[3].Value

    if ($Version) {
        if ($Version -notmatch '^\d+\.\d+\.\d+$') { Fail "버전 형식이 잘못됐습니다: $Version (예: 1.2.3)" }
        $newVersion = $Version
        $reason = '직접 지정'
    }
    elseif ($Major) {
        $newVersion = '{0}.0.0' -f ($curMajor + 1)
        $reason = '큰 변경'
    }
    elseif ($Minor) {
        $newVersion = '{0}.{1}.0' -f $curMajor, ($curMinor + 1)
        $reason = '기능 추가'
    }
    elseif (TagExists "v$current") {
        # 현재 버전은 이미 나갔으니 다음 수정 버전으로 올린다.
        $newVersion = '{0}.{1}.{2}' -f $curMajor, $curMinor, ($curPatch + 1)
        $reason = "v$current 은 이미 배포됨"
    }
    else {
        # 아직 한 번도 나가지 않은 버전이면 그대로 쓴다. 첫 릴리스가 여기 해당한다.
        $newVersion = $current
        $reason = '아직 배포되지 않은 버전'
    }

    if (TagExists "v$newVersion") {
        Fail "태그 v$newVersion 이 이미 있습니다. -Version 으로 다른 버전을 지정하세요."
    }
}

$Tag = "v$newVersion"
$suffix = if ($reason) { " ($reason)" } else { '' }

if ($ShowVersion) {
    Step "다음 릴리스는 $Tag$suffix"
    Pop-Location
    exit 0
}

# ── 3. csproj 버전 갱신 ───────────────────────────────────────

if ($newVersion -ne $current) {
    Step "버전 $current → $newVersion$suffix"

    # XML 로 다시 쓰면 주석과 들여쓰기가 뭉개지므로 해당 줄만 문자열로 교체한다.
    $script:CsprojOriginal = $csprojText
    $updated = $csprojText.Remove($match.Index, $match.Length).Insert($match.Index, "<Version>$newVersion</Version>")
    [System.IO.File]::WriteAllText($Project, $updated, (New-Object System.Text.UTF8Encoding $false))
}
else {
    Step "버전 $newVersion$suffix"
}

# ── 4. Native AOT 게시 ────────────────────────────────────────
Step 'Native AOT 게시 (몇 분 걸릴 수 있습니다)'

if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }

& dotnet publish $Project -c Release -r win-x64 -o $PublishDir --nologo
if ($LASTEXITCODE -ne 0) { Fail '게시에 실패했습니다. 위 빌드 로그를 확인하세요.' }

# ── 5. 필수 파일 검증 ─────────────────────────────────────────
# XBF 와 PRI 는 게시 목록에서 조용히 빠지기 쉬운 파일이다.
# 빠진 채로 배포하면 사용자 PC 에서 실행 즉시 XamlParseException 으로 죽는다.
Step '필수 파일 검증'

$required = @(
    'WinCustoms.exe'
    'WinCustoms.pri'
    'App.xbf'
    'MainWindow.xbf'
    'Views\TweakListPage.xbf'
    'Microsoft.ui.xaml.dll'
)

foreach ($file in $required) {
    if (-not (Test-Path (Join-Path $PublishDir $file))) {
        Fail "게시 결과에 '$file' 이(가) 없습니다. 이대로 배포하면 실행되지 않습니다."
    }
}

$publishSize = [math]::Round((Get-ChildItem $PublishDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Note "$($required.Count)개 필수 파일 확인, 게시 폴더 $publishSize MB"

# ── 6. 실행 확인 ──────────────────────────────────────────────
if (-not $SkipSmokeTest) {
    Step '실행 확인'

    $proc = Start-Process (Join-Path $PublishDir 'WinCustoms.exe') -PassThru
    $windowShown = $false

    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 250
        if ($proc.HasExited) { break }

        $proc.Refresh()
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { $windowShown = $true; break }
    }

    if ($proc.HasExited) {
        Fail ("실행하자마자 종료되었습니다 (종료 코드 0x{0:X8}). %LOCALAPPDATA%\WinCustoms\startup.log 를 확인하세요." -f $proc.ExitCode)
    }

    Stop-Process -Id $proc.Id -Force
    if (-not $windowShown) { Fail '15초 안에 창이 뜨지 않았습니다.' }

    Note '정상 실행 확인'
}

# ── 7. zip 압축 ───────────────────────────────────────────────
# 압축을 풀면 WinCustoms 폴더 하나가 나오도록 스테이징을 거친다.
# 폴더 없이 파일 수백 개가 쏟아지면 사용자가 곤란해진다.
Step 'zip 압축'

if (-not (Test-Path $DistDir)) { New-Item -ItemType Directory -Path $DistDir | Out-Null }

$stage = Join-Path $DistDir 'WinCustoms'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
Copy-Item $PublishDir $stage -Recurse

$zipPath = Join-Path $DistDir "WinCustoms-$newVersion-win-x64.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $stage, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $true)

Remove-Item $stage -Recurse -Force

$zipSize = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
$sha256  = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
Note "$(Split-Path -Leaf $zipPath) / $zipSize MB"
Note "SHA256 $sha256"

if ($SkipUpload) {
    Step '완료 (업로드 생략)'
    Note $zipPath
    Pop-Location
    exit 0
}

# ── 8. 버전 커밋 & 푸시 ───────────────────────────────────────
# 여기까지 왔으면 배포 가능한 물건이 나온 것이 확인됐다. 이제서야 기록을 남긴다.
if ($script:CsprojOriginal) {
    Step "버전 커밋 & 푸시"

    git -C $Root add -- $Project
    git -C $Root commit -m "버전 $newVersion" --quiet
    if ($LASTEXITCODE -ne 0) { Fail '버전 커밋에 실패했습니다.' }

    # 커밋에 성공했으니 되돌리기용 원본은 버린다.
    $script:CsprojOriginal = $null

    git -C $Root push origin HEAD --quiet
    if ($LASTEXITCODE -ne 0) { Fail '푸시에 실패했습니다. git push 후 다시 실행하세요.' }
}

$headSha = (git -C $Root rev-parse HEAD).Trim()

$onRemote = git -C $Root branch --remotes --contains $headSha
if (-not $onRemote) { Fail '현재 커밋이 원격에 없습니다. git push 를 먼저 하세요.' }

# ── 9. 릴리스 생성 ────────────────────────────────────────────
Step "GitHub 릴리스 $Tag 생성"

$notes = @"
## 설치

설치 관리자가 없습니다. zip 을 풀고 ``WinCustoms\WinCustoms.exe`` 를 실행하세요.
.NET 런타임이나 Windows App SDK 를 따로 설치할 필요가 없습니다.

폴더 전체가 있어야 동작합니다. exe 만 따로 빼내면 실행되지 않습니다.

## 요구 사항

- Windows 10 1809(빌드 17763) 이상, 64비트
- 레지스트리의 HKLM 영역을 건드리는 트윅은 적용할 때만 UAC 승인을 요청합니다

## 주의

시스템 레지스트리를 변경하는 도구입니다. 트윅을 적용하기 전에 앱 우측 상단의
[복원 지점] 으로 시스템 복원 지점을 만들어 두는 것을 권합니다.
적용 직전 상태는 ``%LOCALAPPDATA%\WinCustoms\Backups`` 에 .reg 파일로 자동 저장됩니다.

## 파일

| 파일 | 크기 | SHA256 |
| --- | --- | --- |
| ``$(Split-Path -Leaf $zipPath)`` | $zipSize MB | ``$sha256`` |
"@

$notesFile = Join-Path ([System.IO.Path]::GetTempPath()) "wincustoms-release-notes-$newVersion.md"
Set-Content -LiteralPath $notesFile -Value $notes -Encoding UTF8

$ghArgs = @(
    'release', 'create', $Tag, $zipPath
    '--title', "WinCustoms $newVersion"
    '--notes-file', $notesFile
    '--target', $headSha   # 태그가 방금 빌드한 커밋을 정확히 가리키게 한다
)
if ($Draft) { $ghArgs += '--draft' }

& gh @ghArgs
$ghExit = $LASTEXITCODE
Remove-Item $notesFile -Force -ErrorAction SilentlyContinue

if ($ghExit -ne 0) { Fail '릴리스 생성에 실패했습니다.' }

Step '완료'
& gh release view $Tag --json url --jq '.url'

# 태그는 GitHub 쪽에만 생겼으므로 로컬로 가져와 히스토리를 맞춰 둔다.
git -C $Root fetch origin --tags --quiet

Pop-Location
