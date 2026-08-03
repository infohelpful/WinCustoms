#Requires -Version 5.1
<#
.SYNOPSIS
    버전은 그대로 두고, GitHub 최신(또는 지정) 릴리스의 zip 만 새 빌드로 교체한다.

.DESCRIPTION
    release.ps1 과 달리 <Version> 을 올리지 않는다.
    기존 릴리스 자산을 지운 뒤 방금 만든 zip 을 다시 올린다.
    핫픽스·UI 수정처럼 "같은 버전 번호로 파일만 갈아끼울 때" 쓴다.

.PARAMETER Message
    커밋되지 않은 변경을 이 메시지로 커밋한 뒤 푸시한다.
    주지 않으면 변경이 남아 있을 때 멈춘다.

.PARAMETER Tag
    교체할 릴리스 태그. 예: v1.0.0
    생략하면 GitHub 의 최신 릴리스를 쓴다.

.PARAMETER SkipUpload
    빌드와 zip 까지만 한다. 원격 릴리스는 건드리지 않는다.

.PARAMETER SkipSmokeTest
    게시된 exe 실행 확인을 건너뛴다.

.PARAMETER SkipPush
    코드 푸시와 릴리스 대상 커밋 갱신을 건너뛰고, 자산 교체만 한다.
    (로컬 빌드만 올려야 할 때)

.EXAMPLE
    .\scripts\replace-release.ps1 -Message "트윅 적용 스레드 오류 수정"
    수정 커밋 후 최신 릴리스 zip 을 교체한다.

.EXAMPLE
    .\scripts\replace-release.ps1 -Tag v1.0.0
    이미 커밋된 상태에서 v1.0.0 자산만 갈아끼운다.
#>
[CmdletBinding()]
param(
    [string]$Message,
    [string]$Tag,
    [switch]$SkipUpload,
    [switch]$SkipSmokeTest,
    [switch]$SkipPush
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$Root       = Split-Path -Parent $PSScriptRoot
$Project    = Join-Path $Root 'src\WinCustoms\WinCustoms.csproj'
$PublishDir = Join-Path $Root 'publish'
$DistDir    = Join-Path $Root 'dist'

function Step([string]$text) { Write-Host ''; Write-Host "==> $text" -ForegroundColor Cyan }
function Note([string]$text) { Write-Host "    $text" -ForegroundColor DarkGray }

function Fail([string]$text) {
    Write-Host ''; Write-Host "!!  $text" -ForegroundColor Red
    Pop-Location -ErrorAction SilentlyContinue
    exit 1
}

# requireAdministrator 앱은 비관리자 셸에서 Stop-Process 가 Access Denied 난다.
function Stop-ManagedProcess([System.Diagnostics.Process]$proc) {
    if ($null -eq $proc) { return }
    $id = $proc.Id
    try {
        if (-not $proc.HasExited) {
            Stop-Process -Id $id -Force -ErrorAction Stop
        }
    }
    catch {
        $taskkill = Join-Path $env:SystemRoot 'System32\taskkill.exe'
        $code = RunQuiet $taskkill @('/F', '/PID', "$id")
        if ($code -ne 0) {
            Note "관리자 권한으로 프로세스($id) 종료 시도…"
            try {
                $p = Start-Process -FilePath $taskkill -ArgumentList @('/F', '/PID', "$id") `
                    -Verb RunAs -Wait -PassThru -WindowStyle Hidden
                if ($null -ne $p -and $p.ExitCode -ne 0 -and $p.ExitCode -ne 128) {
                    Fail "스모크 테스트 프로세스를 종료하지 못했습니다 (PID $id). 작업 관리자에서 WinCustoms 를 닫고 다시 실행하세요."
                }
            }
            catch {
                Fail "스모크 테스트 프로세스를 종료하지 못했습니다 (PID $id). 작업 관리자에서 WinCustoms 를 닫고 다시 실행하세요."
            }
        }
    }

    try { $proc.WaitForExit(10000) | Out-Null } catch { }
}

function Stop-AllWinCustoms {
    $taskkill = Join-Path $env:SystemRoot 'System32\taskkill.exe'
    $procs = @(Get-Process -Name 'WinCustoms' -ErrorAction SilentlyContinue)
    if ($procs.Count -eq 0) { return }

    Note "실행 중인 WinCustoms $($procs.Count)개 종료 (publish 폴더 잠금 해제)…"
    foreach ($proc in $procs) {
        Stop-ManagedProcess $proc
    }

    Start-Sleep -Seconds 1
    $left = @(Get-Process -Name 'WinCustoms' -ErrorAction SilentlyContinue)
    if ($left.Count -gt 0) {
        Note "남은 프로세스에 관리자 taskkill /IM 시도…"
        try {
            Start-Process -FilePath $taskkill -ArgumentList @('/F', '/IM', 'WinCustoms.exe') `
                -Verb RunAs -Wait -WindowStyle Hidden | Out-Null
        }
        catch { }
        Start-Sleep -Seconds 1
    }

    $still = @(Get-Process -Name 'WinCustoms' -ErrorAction SilentlyContinue)
    if ($still.Count -gt 0) {
        Fail "WinCustoms 가 아직 실행 중이라 publish 폴더를 비울 수 없습니다. 작업 관리자에서 종료 후 다시 실행하세요."
    }
}

function Clear-PublishDirectory {
    if (-not (Test-Path $PublishDir)) { return }

    Stop-AllWinCustoms

    $attempt = 0
    while ($attempt -lt 5) {
        $attempt++
        try {
            Remove-Item $PublishDir -Recurse -Force -ErrorAction Stop
            return
        }
        catch {
            Note "publish 삭제 재시도 $attempt/5 — $($_.Exception.Message)"
            Stop-AllWinCustoms
            Start-Sleep -Seconds 1
        }
    }

    Fail "publish 폴더를 비우지 못했습니다. WinCustoms/탐색기에서 해당 폴더를 닫고 다시 실행하세요.`n$PublishDir"
}

function RunQuiet([string]$exe, [string[]]$cmdArgs) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $exe @cmdArgs 2>&1 | Out-Null
        return $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previous }
}

Push-Location $Root

# ── 1. 사전 점검 ──────────────────────────────────────────────
Step '사전 점검 (버전 유지 · 자산 교체)'

$csprojText = [System.IO.File]::ReadAllText($Project)
$match = [regex]::Match($csprojText, '<Version>\s*(\d+)\.(\d+)\.(\d+)\s*</Version>')
if (-not $match.Success) {
    Fail 'csproj 에서 <Version>x.y.z</Version> 을 찾지 못했습니다.'
}

$version = '{0}.{1}.{2}' -f $match.Groups[1].Value, $match.Groups[2].Value, $match.Groups[3].Value
Note "csproj 버전 $version (올리지 않음)"

if (-not $SkipUpload) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        Fail 'GitHub CLI(gh) 를 찾을 수 없습니다. winget install GitHub.cli 로 설치하세요.'
    }

    $dirty = git -C $Root status --porcelain
    if ($dirty -and $Message) {
        git -C $Root add -A
        git -C $Root commit -m $Message --quiet
        if ($LASTEXITCODE -ne 0) { Fail '커밋에 실패했습니다.' }
        Note "변경 사항을 커밋했습니다: $Message"
        $dirty = $null
    }

    if ($dirty) {
        Fail ("커밋되지 않은 변경이 있습니다. 먼저 커밋하거나 -Message 로 커밋 메시지를 넘기세요.`n" + ($dirty -join "`n"))
    }

    git -C $Root fetch origin --tags --quiet
    if ($LASTEXITCODE -ne 0) { Fail 'origin 에서 fetch 하지 못했습니다.' }

    $branch = (git -C $Root rev-parse --abbrev-ref HEAD).Trim()
    if ((RunQuiet 'git' @('-C', $Root, 'rev-parse', '--verify', '--quiet', "origin/$branch")) -eq 0) {
        $behind = [int](git -C $Root rev-list --count "HEAD..origin/$branch").Trim()
        if ($behind -gt 0) {
            Fail "원격에 내 로컬보다 새 커밋이 $behind 개 있습니다. git pull 을 먼저 하세요."
        }
    }

    if (-not $Tag) {
        $Tag = (gh release view --json tagName --jq '.tagName' 2>$null)
        if (-not $Tag) {
            Fail '최신 GitHub 릴리스를 찾지 못했습니다. -Tag v1.0.0 처럼 지정하세요.'
        }
        Note "최신 릴리스 태그: $Tag"
    }
    else {
        if ($Tag -notmatch '^v') { $Tag = "v$Tag" }
        if ((RunQuiet 'gh' @('release', 'view', $Tag)) -ne 0) {
            Fail "릴리스 $Tag 을(를) 찾을 수 없습니다."
        }
        Note "교체 대상 릴리스: $Tag"
    }
}
else {
    if (-not $Tag) { $Tag = "v$version" }
    elseif ($Tag -notmatch '^v') { $Tag = "v$Tag" }
    Note "업로드 생략 - zip 이름용 태그 $Tag"
}

# zip 파일명은 보통 태그 버전(v 제거)과 맞춘다. csproj 와 다를 수 있으니 태그 기준.
$releaseVersion = $Tag.TrimStart('v')
if ($releaseVersion -notmatch '^\d+\.\d+\.\d+$') {
    # 태그가 특이하면 csproj 버전으로 파일명
    $releaseVersion = $version
}

# ── 2. Native AOT 게시 ────────────────────────────────────────
Step 'Native AOT 게시 (몇 분 걸릴 수 있습니다)'

Clear-PublishDirectory

. (Join-Path $PSScriptRoot 'Publish-Aot.ps1')
try {
    Invoke-WinCustomsAotPublish -Project $Project -PublishDir $PublishDir
}
catch {
    Fail $_.Exception.Message
}

# ── 3. 필수 파일 검증 ─────────────────────────────────────────
Step '필수 파일 검증'

$required = @(
    'WinCustoms.exe'
    'WinCustoms.pri'
    'App.xbf'
    'MainWindow.xbf'
    'Views\TweakListPage.xbf'
    'Microsoft.ui.xaml.dll'
    'Assets\WinCustoms.ico'
    'Assets\AppIcon.png'
)

foreach ($file in $required) {
    if (-not (Test-Path (Join-Path $PublishDir $file))) {
        Fail "게시 결과에 '$file' 이(가) 없습니다. 이대로 배포하면 실행되지 않습니다."
    }
}

$publishSize = [math]::Round((Get-ChildItem $PublishDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Note "$($required.Count)개 필수 파일 확인, 게시 폴더 $publishSize MB"

# ── 4. 실행 확인 ──────────────────────────────────────────────
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

    Stop-ManagedProcess $proc
    if (-not $windowShown) { Fail '15초 안에 창이 뜨지 않았습니다.' }

    Note '정상 실행 확인'
}

# ── 5. zip 압축 ───────────────────────────────────────────────
Step 'zip 압축'

if (-not (Test-Path $DistDir)) { New-Item -ItemType Directory -Path $DistDir | Out-Null }

$stage = Join-Path $DistDir 'WinCustoms'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
Copy-Item $PublishDir $stage -Recurse

$zipName = "WinCustoms-$releaseVersion-win-x64.zip"
$zipPath = Join-Path $DistDir $zipName
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $stage, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $true)

Remove-Item $stage -Recurse -Force

$zipSize = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
$sha256  = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
Note "$zipName / $zipSize MB"
Note "SHA256 $sha256"

if ($SkipUpload) {
    Step '완료 (업로드 생략)'
    Note $zipPath
    Pop-Location
    exit 0
}

# ── 6. 코드 푸시 (선택) ───────────────────────────────────────
$headSha = (git -C $Root rev-parse HEAD).Trim()

if (-not $SkipPush) {
    Step '커밋 푸시'
    git -C $Root push origin HEAD --quiet
    if ($LASTEXITCODE -ne 0) { Fail '푸시에 실패했습니다.' }
    $headSha = (git -C $Root rev-parse HEAD).Trim()
    Note "origin 에 $($headSha.Substring(0, 7)) 까지 푸시됨"
}
else {
    Note "푸시 생략 - 릴리스 대상 커밋은 갱신하지 않습니다."
}

# ── 7. 기존 자산 삭제 후 재업로드 ─────────────────────────────
Step "릴리스 $Tag 자산 교체"

$assetJson = gh release view $Tag --json assets --jq '.assets[].name' 2>$null
$oldAssets = @()
if ($assetJson) {
    $oldAssets = @($assetJson | Where-Object { $_ -and $_.Trim() })
}

if ($oldAssets.Count -gt 0) {
    foreach ($asset in $oldAssets) {
        Note "삭제: $asset"
        & gh release delete-asset $Tag $asset --yes
        if ($LASTEXITCODE -ne 0) { Fail "자산 삭제 실패: $asset" }
    }
}
else {
    Note '기존 자산 없음 - 새로 업로드합니다.'
}

& gh release upload $Tag $zipPath --clobber
if ($LASTEXITCODE -ne 0) { Fail 'zip 업로드에 실패했습니다.' }
Note "업로드: $zipName"

# 릴리스 노트에 SHA 를 갱신 (설치 안내 유지)
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
| ``$zipName`` | $zipSize MB | ``$sha256`` |

> 이 빌드는 버전 번호($releaseVersion)를 유지한 채 자산을 교체한 것입니다.
"@

$notesFile = Join-Path ([System.IO.Path]::GetTempPath()) "wincustoms-replace-notes-$releaseVersion.md"
Set-Content -LiteralPath $notesFile -Value $notes -Encoding UTF8

$editArgs = @(
    'release', 'edit', $Tag
    '--notes-file', $notesFile
)
if (-not $SkipPush) {
    $editArgs += @('--target', $headSha)
}

& gh @editArgs
$editExit = $LASTEXITCODE
Remove-Item $notesFile -Force -ErrorAction SilentlyContinue

if ($editExit -ne 0) {
    Write-Host '    경고: 릴리스 노트/대상 커밋 갱신에 실패했습니다. zip 업로드는 완료됐을 수 있습니다.' -ForegroundColor Yellow
}

Step '완료'
& gh release view $Tag --json url --jq '.url'
Note "버전 $releaseVersion 유지 · 자산만 교체됨"

git -C $Root fetch origin --tags --quiet

Pop-Location
