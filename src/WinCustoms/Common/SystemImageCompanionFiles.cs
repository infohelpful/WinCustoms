using System.Text;

namespace WinCustoms.Common;

/// <summary>
/// WIM 옆 복원 스크립트 + WinRE 자동 복원 플래그/부트 스크립트.
/// </summary>
public static class SystemImageCompanionFiles
{
    public const string RestoreCmdFileName = "복원-C드라이브.cmd";
    public const string GuideFileName = "복원안내.txt";
    public const string AutoRestoreFlagFileName = "WinCustoms-AutoRestore.flag";
    public const string WinReBootstrapFileName = "WinCustomsWinREBoot.cmd";

    public static string GetRestoreCmdPath(string imageFile)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(imageFile))!, RestoreCmdFileName);

    public static string GetAutoRestoreFlagPath(string imageFile)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(imageFile))!, AutoRestoreFlagFileName);

    public static void Write(string imageFile, string imageName)
    {
        var fullImage = Path.GetFullPath(imageFile);
        var dir = Path.GetDirectoryName(fullImage)
                  ?? throw new InvalidOperationException("이미지 경로가 올바르지 않습니다.");
        var wimName = Path.GetFileName(fullImage);

        Directory.CreateDirectory(dir);

        var guidePath = Path.Combine(dir, GuideFileName);
        var cmdPath = Path.Combine(dir, RestoreCmdFileName);

        var guide = $"""
            WinCustoms — C: Windows 복구 백업
            =================================

            파일: {wimName}
            이름: {imageName}
            만든 시각: {DateTime.Now:yyyy-MM-dd HH:mm:ss}

            【추천】 WinCustoms에서 「C: 자동 복원」
            → WinRE로 다시 시작되면 검은 화면에서 자동으로 C:에 백업을 적용합니다.
            → 명령 프롬프트를 찾을 필요 없습니다.
            → 백업 USB/외장이 연결된 채로 다시 시작하세요.

            【수동 비상】 Windows가 안 켜질 때
            1) 강제 종료 2~3회로 WinRE 진입 → 명령 프롬프트
            2) USB 문자 확인 후 {RestoreCmdFileName} 실행

            BitLocker가 켜져 있으면 WinRE에서 잠금 해제 후 진행하세요.
            """;

        var cmd = $"""
            @echo off
            setlocal EnableExtensions
            cd /d "%~dp0"
            title WinCustoms - Windows C restore
            echo.
            echo === WinCustoms manual C: restore ===
            echo Run from WinRE Command Prompt only.
            echo.

            set "WIM=%~dp0{wimName}"
            if not exist "%WIM%" (
              echo ERROR: WIM not found: %WIM%
              pause
              exit /b 1
            )

            set "WINVOL="
            for %%D in (C D E F G H I J K L M N O P Q R S T U V W Y Z) do (
              if exist "%%D:\Windows\System32\config\SOFTWARE" if exist "%%D:\Windows\System32\ntoskrnl.exe" (
                set "WINVOL=%%D:"
                goto :found
              )
            )
            :found
            if "%WINVOL%"=="" (
              echo ERROR: Windows partition not found.
              pause
              exit /b 1
            )

            echo Image : %WIM%
            echo Target: %WINVOL%
            pause
            dism.exe /Apply-Image /ImageFile:"%WIM%" /Index:1 /ApplyDir:%WINVOL%\ /CheckIntegrity
            if errorlevel 1 ( pause & exit /b 1 )
            bcdboot.exe %WINVOL%\Windows /f UEFI
            echo Done. Close window and Continue to reboot.
            pause
            exit /b 0
            """;

        File.WriteAllText(guidePath, guide, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        File.WriteAllText(cmdPath, cmd, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    /// <summary>드라이브 루트에 두는 일회용 자동 복원 플래그. WinRE 부트스트랩이 드라이브 문자만 다시 찾으면 된다.</summary>
    public static void WriteAutoRestoreFlag(string imageFile)
    {
        var fullImage = Path.GetFullPath(imageFile);
        if (!File.Exists(fullImage))
            throw new FileNotFoundException("WIM 파일을 찾을 수 없습니다.", fullImage);

        Write(fullImage, Path.GetFileNameWithoutExtension(fullImage));

        var root = Path.GetPathRoot(fullImage)
                   ?? throw new InvalidOperationException("이미지 드라이브 루트를 알 수 없습니다.");
        var relative = Path.GetRelativePath(root, fullImage);
        if (relative.StartsWith("..", StringComparison.Ordinal))
            throw new InvalidOperationException("WIM 경로를 드라이브 기준으로 표현할 수 없습니다.");

        // 드라이브 루트 + WIM 옆 둘 다 둔다(문자가 바뀌어도 루트 플래그를 우선 탐색).
        var content = $"""
            WIM={relative.Replace('/', '\\')}
            CREATED={DateTime.Now:O}
            """;

        File.WriteAllText(Path.Combine(root, AutoRestoreFlagFileName), content,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(GetAutoRestoreFlagPath(fullImage), content,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>WinRE 이미지 안에 넣을 부트 래퍼. 플래그가 있으면 자동 복원, 없으면 기본 복구 UI.</summary>
    public static string BuildWinReBootstrapScript()
        => """
            @echo off
            setlocal EnableExtensions
            title WinCustoms Auto Restore
            echo.
            echo ========================================
            echo   WinCustoms
            echo ========================================
            echo.

            set "FLAGFILE="
            set "FLAGDRIVE="
            for %%D in (C D E F G H I J K L M N O P Q R S T U V W Y Z) do (
              if exist "%%D:\WinCustoms-AutoRestore.flag" (
                set "FLAGFILE=%%D:\WinCustoms-AutoRestore.flag"
                set "FLAGDRIVE=%%D:"
                goto :haveflag
              )
            )

            :haveflag
            if "%FLAGFILE%"=="" goto :normalui

            echo Auto-restore flag found: %FLAGFILE%
            echo.
            set "WIMNAME="
            for /f "usebackq tokens=1,* delims==" %%A in ("%FLAGFILE%") do (
              if /i "%%A"=="WIM" set "WIMNAME=%%B"
            )
            if "%WIMNAME%"=="" (
              echo ERROR: WIM= missing in flag file.
              goto :fail
            )

            set "WIMFILE=%FLAGDRIVE%\%WIMNAME%"
            rem WIMNAME may include subfolders, e.g. Backups\file.wim
            if not exist "%WIMFILE%" (
              echo ERROR: WIM not found: %WIMFILE%
              echo Keep the USB/external drive plugged in.
              goto :fail
            )

            echo Searching Windows partition...
            set "WINVOL="
            for %%D in (C D E F G H I J K L M N O P Q R S T U V W Y Z) do (
              if exist "%%D:\Windows\System32\config\SOFTWARE" if exist "%%D:\Windows\System32\ntoskrnl.exe" (
                set "WINVOL=%%D:"
                goto :foundwin
              )
            )
            :foundwin
            if "%WINVOL%"=="" (
              echo ERROR: Windows partition not found.
              goto :fail
            )

            echo.
            echo Image : %WIMFILE%
            echo Target: %WINVOL%
            echo.
            echo Applying backup. This takes a long time. Do not power off.
            echo.

            rem One-shot: remove flag first so a reboot won't loop forever on failure mid-way after partial apply.
            rem Actually delete AFTER success — if we delete first and fail, user can retry by recreating flag.
            rem Delete after success only:

            dism.exe /Apply-Image /ImageFile:"%WIMFILE%" /Index:1 /ApplyDir:%WINVOL%\ /CheckIntegrity
            if errorlevel 1 (
              echo DISM failed.
              goto :fail
            )

            echo Updating boot...
            bcdboot.exe %WINVOL%\Windows /f UEFI

            del /f /q "%FLAGFILE%" >nul 2>&1

            echo.
            echo Restore finished. Rebooting in 8 seconds...
            ping -n 9 127.0.0.1 >nul
            wpeutil reboot
            exit /b 0

            :fail
            echo.
            echo Auto-restore failed. Opening Command Prompt.
            echo You can run 복원-C드라이브.cmd from the USB folder.
            echo.
            echo Flag file left in place for retry: %FLAGFILE%
            echo.
            start "WinCustoms" %SYSTEMROOT%\System32\cmd.exe
            exit /b 1

            :normalui
            echo No auto-restore flag. Starting Windows Recovery...
            if exist "%SYSTEMROOT%\System32\Recovery\RecEnv.exe" (
              "%SYSTEMROOT%\System32\Recovery\RecEnv.exe"
              goto :afterui
            )
            if exist "%SYSTEMDRIVE%\sources\recovery\RecEnv.exe" (
              "%SYSTEMDRIVE%\sources\recovery\RecEnv.exe"
              goto :afterui
            )
            if exist "X:\sources\recovery\RecEnv.exe" (
              "X:\sources\recovery\RecEnv.exe"
              goto :afterui
            )
            echo RecEnv.exe not found. Opening cmd.
            start "WinRE" %SYSTEMROOT%\System32\cmd.exe
            :afterui
            exit /b 0
            """;
}
