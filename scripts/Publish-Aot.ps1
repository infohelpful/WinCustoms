#Requires -Version 5.1
<#
.SYNOPSIS
    Native AOT 게시에 필요한 MSVC 환경을 잡고 dotnet publish 를 실행한다.

.DESCRIPTION
    ILCompiler 기본 findvcvarsall.bat 은 vswhere -requires VC.Tools 가 Build Tools 에서
    빈 값을 돌려 "Platform linker not found" 로 자주 실패한다.
    이 스크립트는 vcvarsall.bat 을 직접 찾은 뒤 IlcUseEnvironmentalTools=true 로 게시한다.
#>

function Find-VcVarsAllBat {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $installs = @(
            & $vswhere -latest -prerelease -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2>$null
            & $vswhere -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2>$null
            & $vswhere -products * -property installationPath 2>$null
        ) | Where-Object { $_ -and $_.Trim().Length -gt 0 } | Select-Object -Unique

        foreach ($install in $installs) {
            $bat = Join-Path $install 'VC\Auxiliary\Build\vcvarsall.bat'
            if (Test-Path -LiteralPath $bat) { return $bat }
        }
    }

    foreach ($bat in @(
            (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvarsall.bat'),
            (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvarsall.bat'),
            (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvarsall.bat'),
            (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvarsall.bat'),
            (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\2022\Enterprise\VC\Auxiliary\Build\vcvarsall.bat'),
            (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2019\BuildTools\VC\Auxiliary\Build\vcvarsall.bat')
        )) {
        if (Test-Path -LiteralPath $bat) { return $bat }
    }

    return $null
}

function Invoke-WinCustomsAotPublish {
    param(
        [Parameter(Mandatory)][string]$Project,
        [Parameter(Mandatory)][string]$PublishDir
    )

    $vcvars = Find-VcVarsAllBat
    if (-not $vcvars) {
        throw @"
MSVC 링커(vcvarsall.bat)를 찾지 못했습니다.
Visual Studio Installer 에서 「C++를 사용한 데스크톱 개발」 또는
「MSVC v143 - VS 2022 C++ x64/x86 빌드 도구」를 설치한 뒤 다시 실행하세요.
"@
    }

    Write-Host "    MSVC 환경: $vcvars" -ForegroundColor DarkGray

    # vcvarsall 이 stderr/exit 로 "Unknown error" 를 내는 경우가 있어 && 체인하면
    # 링크는 됐는데도 publish 가 스킵된다. call 뒤는 & 로 이어 간다.
    $bat = Join-Path $env:TEMP ('wc-aot-publish-' + [guid]::NewGuid().ToString('N') + '.bat')
    $lines = @"
@echo off
call "$vcvars" amd64 >nul
where link >nul 2>&1
if errorlevel 1 (
  echo MSVC link.exe not on PATH after vcvarsall.
  exit /b 1
)
dotnet publish "$Project" -c Release -r win-x64 -o "$PublishDir" --nologo -p:IlcUseEnvironmentalTools=true
exit /b %ERRORLEVEL%
"@
    try {
        [System.IO.File]::WriteAllText($bat, $lines)

        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            cmd.exe /c "`"$bat`""
            $code = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previous
        }
    }
    finally {
        Remove-Item -LiteralPath $bat -Force -ErrorAction SilentlyContinue
    }

    if ($code -ne 0) {
        throw "dotnet publish 실패 (종료 코드 $code). MSVC C++ 빌드 도구가 설치돼 있는지 확인하세요."
    }
}
