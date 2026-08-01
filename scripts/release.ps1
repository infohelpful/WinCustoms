#Requires -Version 5.1
<#
.SYNOPSIS
    WinCustoms 배포본을 빌드하고 GitHub Releases 에 올린다.

.DESCRIPTION
    Native AOT 로 게시 → 필수 파일 검증 → 실행 확인 → zip 압축 → 릴리스 생성 순으로 진행한다.
    중간에 하나라도 실패하면 업로드까지 가지 않고 멈춘다.

.PARAMETER Version
    릴리스 버전. 생략하면 csproj 의 <Version> 값을 쓴다.

.PARAMETER Draft
    공개하지 않고 초안(draft) 으로 만든다. 내용을 확인한 뒤 웹에서 게시할 때 쓴다.

.PARAMETER SkipUpload
    zip 까지만 만들고 GitHub 업로드는 하지 않는다. 결과물만 확인하고 싶을 때 쓴다.

.PARAMETER SkipSmokeTest
    게시된 exe 를 실제로 띄워 보는 검증을 건너뛴다.

.EXAMPLE
    .\scripts\release.ps1 -SkipUpload
    빌드와 zip 까지만 해 보고 결과물을 확인한다.

.EXAMPLE
    .\scripts\release.ps1
    csproj 버전으로 태그를 만들고 릴리스를 게시한다.
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$Draft,
    [switch]$SkipUpload,
    [switch]$SkipSmokeTest
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

function Step([string]$text) { Write-Host ''; Write-Host "==> $text" -ForegroundColor Cyan }
function Note([string]$text) { Write-Host "    $text" -ForegroundColor DarkGray }
function Fail([string]$text) { Write-Host ''; Write-Host "!!  $text" -ForegroundColor Red; exit 1 }

# ── 1. 버전 결정 ──────────────────────────────────────────────
Step '버전 확인'

if (-not $Version) {
    $csproj = [xml](Get-Content -LiteralPath $Project -Raw)
    $node = $csproj.SelectSingleNode('//PropertyGroup/Version')
    if (-not $node) { Fail 'csproj 에서 <Version> 을 찾지 못했습니다. -Version 으로 직접 지정하세요.' }
    $Version = $node.InnerText.Trim()
}

$Tag = "v$Version"
Note "버전 $Version (태그 $Tag)"

# ── 2. 업로드 전 사전 점검 ────────────────────────────────────
if (-not $SkipUpload) {
    Step '저장소 상태 점검'

    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        Fail 'GitHub CLI(gh) 를 찾을 수 없습니다. winget install GitHub.cli 로 설치하세요.'
    }

    $dirty = git -C $Root status --porcelain
    if ($dirty) {
        Fail ("커밋되지 않은 변경이 있습니다. 먼저 커밋하거나 -SkipUpload 로 실행하세요.`n" + ($dirty -join "`n"))
    }

    git -C $Root rev-parse --verify --quiet "refs/tags/$Tag" > $null 2>&1
    if ($LASTEXITCODE -eq 0) { Fail "태그 $Tag 가 이미 있습니다. 버전을 올리세요." }

    Note '작업 트리 깨끗함, 태그 사용 가능'
}

# ── 3. Native AOT 게시 ────────────────────────────────────────
Step 'Native AOT 게시 (몇 분 걸릴 수 있습니다)'

if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }

& dotnet publish $Project -c Release -r win-x64 -o $PublishDir --nologo
if ($LASTEXITCODE -ne 0) { Fail '게시에 실패했습니다. 위 빌드 로그를 확인하세요.' }

# ── 4. 필수 파일 검증 ─────────────────────────────────────────
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

# ── 5. 실행 확인 ──────────────────────────────────────────────
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

# ── 6. zip 압축 ───────────────────────────────────────────────
# 압축을 풀면 WinCustoms 폴더 하나가 나오도록 스테이징을 거친다.
# 폴더 없이 242개 파일이 쏟아지면 사용자가 곤란해진다.
Step 'zip 압축'

if (-not (Test-Path $DistDir)) { New-Item -ItemType Directory -Path $DistDir | Out-Null }

$stage = Join-Path $DistDir 'WinCustoms'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
Copy-Item $PublishDir $stage -Recurse

$zipPath = Join-Path $DistDir "WinCustoms-$Version-win-x64.zip"
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
    exit 0
}

# ── 7. 릴리스 생성 ────────────────────────────────────────────
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

$notesFile = Join-Path ([System.IO.Path]::GetTempPath()) "wincustoms-release-notes-$Version.md"
Set-Content -LiteralPath $notesFile -Value $notes -Encoding UTF8

$ghArgs = @(
    'release', 'create', $Tag, $zipPath
    '--title', "WinCustoms $Version"
    '--notes-file', $notesFile
)
if ($Draft) { $ghArgs += '--draft' }

& gh @ghArgs
$ghExit = $LASTEXITCODE
Remove-Item $notesFile -Force -ErrorAction SilentlyContinue

if ($ghExit -ne 0) { Fail '릴리스 생성에 실패했습니다.' }

Step '완료'
& gh release view $Tag --json url --jq '.url'
