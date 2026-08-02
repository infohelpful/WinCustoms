using System.Text;

namespace WinCustoms.Common;

/// <summary>
/// WIM 옆 복원 스크립트 + WinRE 자동 캡처/복원 플래그/부트 스크립트.
/// </summary>
public static class SystemImageCompanionFiles
{
    public const string RestoreCmdFileName = "복원-C드라이브.cmd";
    public const string GuideFileName = "복원안내.txt";
    public const string AutoRestoreFlagFileName = "WinCustoms-AutoRestore.flag";
    public const string AutoCaptureFlagFileName = "WinCustoms-AutoCapture.flag";
    public const string WinReBootstrapFileName = "WinCustomsWinREBoot.cmd";

    public static string GetRestoreCmdPath(string imageFile)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(imageFile))!, RestoreCmdFileName);

    public static string GetAutoRestoreFlagPath(string imageFile)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(imageFile))!, AutoRestoreFlagFileName);

    public static string GetAutoCaptureFlagPath(string imageFile)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(imageFile))!, AutoCaptureFlagFileName);

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

            【백업】 WinCustoms에서 「C: 백업 시작」
            → 다시 시작 후 WinRE에서 오프라인으로 .wim 을 만듭니다.
            → USB/외장은 연결한 채로 두세요.

            【복원】 WinCustoms에서 「C: 자동 복원」
            → WinRE로 다시 시작되면 검은 화면에서 자동으로 C:에 백업을 적용합니다.

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
        ClearSiblingFlags(fullImage, AutoCaptureFlagFileName);
        WriteVolumeFlag(fullImage, AutoRestoreFlagFileName, GetAutoRestoreFlagPath(fullImage), imageName: null);
    }

    /// <summary>WinRE에서 오프라인 캡처할 때 쓰는 플래그. WIM 파일은 아직 없어도 된다.</summary>
    public static void WriteAutoCaptureFlag(string imageFile, string imageName)
    {
        var fullImage = Path.GetFullPath(imageFile);
        if (!string.Equals(Path.GetExtension(fullImage), ".wim", StringComparison.OrdinalIgnoreCase))
            fullImage = Path.ChangeExtension(fullImage, ".wim");

        var dir = Path.GetDirectoryName(fullImage)
                  ?? throw new InvalidOperationException("이미지 경로가 올바르지 않습니다.");
        Directory.CreateDirectory(dir);

        var name = string.IsNullOrWhiteSpace(imageName)
            ? Path.GetFileNameWithoutExtension(fullImage)
            : imageName.Trim();
        // 배치/DISM 인자 깨짐 방지
        name = name.Replace('"', '\'').Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (name.Length == 0)
            name = "WinCustoms Backup";

        Write(fullImage, name);
        ClearSiblingFlags(fullImage, AutoRestoreFlagFileName);
        WriteVolumeFlag(fullImage, AutoCaptureFlagFileName, GetAutoCaptureFlagPath(fullImage), name);
    }

    private static void ClearSiblingFlags(string fullImage, string otherRootFlagName)
    {
        try
        {
            var root = Path.GetPathRoot(fullImage);
            if (!string.IsNullOrEmpty(root))
            {
                var rootFlag = Path.Combine(root, otherRootFlagName);
                if (File.Exists(rootFlag)) File.Delete(rootFlag);
            }

            var dir = Path.GetDirectoryName(fullImage);
            if (!string.IsNullOrEmpty(dir))
            {
                var side = Path.Combine(dir, otherRootFlagName);
                if (File.Exists(side)) File.Delete(side);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void WriteVolumeFlag(string fullImage, string rootFlagName, string sideFlagPath, string? imageName)
    {
        var root = Path.GetPathRoot(fullImage)
                   ?? throw new InvalidOperationException("이미지 드라이브 루트를 알 수 없습니다.");
        var relative = Path.GetRelativePath(root, fullImage);
        if (relative.StartsWith("..", StringComparison.Ordinal))
            throw new InvalidOperationException("WIM 경로를 드라이브 기준으로 표현할 수 없습니다.");

        relative = relative.Replace('/', '\\');
        var wimFile = Path.GetFileName(fullImage);

        var content = new StringBuilder();
        // WIM= 드라이브 루트 기준 상대경로 / WIMFILE= 파일명(플래그가 WIM 옆에 있을 때용)
        content.Append("WIM=").Append(relative).AppendLine();
        content.Append("WIMFILE=").Append(wimFile).AppendLine();
        if (!string.IsNullOrWhiteSpace(imageName))
            content.Append("NAME=").Append(imageName).AppendLine();
        content.Append("CREATED=").Append(DateTime.Now.ToString("O")).AppendLine();

        var text = content.ToString();
        // 1) 드라이브 루트  2) WIM 과 같은 폴더 — WinRE 에서 둘 다 찾는다.
        File.WriteAllText(Path.Combine(root, rootFlagName), text,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(sideFlagPath, text,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    /// WinRE 부트 래퍼.
    /// 플래그: 드라이브 루트 + WIM 옆(하위 폴더 dir /s). USB 대기 후 캡처/복원.
    /// </summary>
    public static string BuildWinReBootstrapScript()
        => """
            @echo off
            setlocal EnableExtensions EnableDelayedExpansion
            title WinCustoms
            echo.
            echo ========================================
            echo   WinCustoms
            echo ========================================
            echo.
            echo Waiting for USB / flag (up to ~60 sec)...
            echo Flag is on the backup drive root OR next to the .wim file.
            echo.

            set "CAPFLAG="
            set "CAPDRIVE="
            set "FLAGFILE="
            set "FLAGDRIVE="
            set "TRY=0"

            :findflags
            set "CAPFLAG="
            set "CAPDRIVE="
            set "FLAGFILE="
            set "FLAGDRIVE="

            call :SearchFlag WinCustoms-AutoCapture.flag CAP
            if not "!CAPFLAG!"=="" goto :docapture

            call :SearchFlag WinCustoms-AutoRestore.flag RES
            if not "!FLAGFILE!"=="" goto :dorestore

            set /a TRY+=1
            if !TRY! LSS 30 (
              echo   retry !TRY!/30 ...
              ping -n 3 127.0.0.1 >nul
              goto :findflags
            )

            echo.
            echo ERROR: Flag not found.
            echo Expected: WinCustoms-AutoRestore.flag ^(or AutoCapture^)
            echo   - on USB root, OR
            echo   - in the same folder as the .wim
            echo.
            goto :hold

            rem ---------- CAPTURE ----------
            :docapture
            echo Auto-capture flag: !CAPFLAG!
            call :ReadFlag "!CAPFLAG!"
            call :ResolveWim "!CAPDRIVE!" "!CAPFLAG!"
            if "!WIMFILE!"=="" goto :capfail
            if not exist "!WIMFILE!" (
              echo ERROR: WIM path not found: !WIMFILE!
              goto :capfail
            )

            for %%I in ("!WIMFILE!") do set "WIMDIR=%%~dpI"
            if not exist "!WIMDIR!" mkdir "!WIMDIR!" >nul 2>&1

            call :FindWinVol
            if "!WINVOL!"=="" (
              echo ERROR: Windows partition not found.
              goto :capfail
            )

            set "SCRATCH=!CAPDRIVE!\WinCustoms-DismScratch"
            if not exist "!SCRATCH!" mkdir "!SCRATCH!" >nul 2>&1

            echo Source : !WINVOL!\
            echo Output : !WIMFILE!
            echo Name   : !IMGNAME!
            echo.
            echo Capturing... Do not power off.
            echo.

            if exist "!WIMFILE!" del /f /q "!WIMFILE!" >nul 2>&1
            dism.exe /Capture-Image /ImageFile:"!WIMFILE!" /CaptureDir:!WINVOL!\ /Name:"!IMGNAME!" /Description:"WinCustoms offline backup" /Compress:fast /NoRpFix /ScratchDir:"!SCRATCH!"
            if errorlevel 1 (
              echo DISM capture failed.
              goto :capfail
            )

            call :DeleteFlags "!CAPFLAG!" "!WIMFILE!" WinCustoms-AutoCapture.flag
            rmdir /s /q "!SCRATCH!" >nul 2>&1
            echo Capture finished. Rebooting...
            ping -n 9 127.0.0.1 >nul
            wpeutil reboot
            exit /b 0

            :capfail
            echo Auto-capture FAILED.
            goto :hold

            rem ---------- RESTORE ----------
            :dorestore
            echo Auto-restore flag: !FLAGFILE!
            call :ReadFlag "!FLAGFILE!"
            call :ResolveWim "!FLAGDRIVE!" "!FLAGFILE!"
            if "!WIMFILE!"=="" goto :fail
            if not exist "!WIMFILE!" (
              echo ERROR: WIM not found: !WIMFILE!
              echo Keep USB plugged in. Flag folder: !FLAGFILE!
              goto :fail
            )

            call :FindWinVol
            if "!WINVOL!"=="" (
              echo ERROR: Windows partition not found.
              goto :fail
            )

            echo Image : !WIMFILE!
            echo Target: !WINVOL!
            echo.
            echo Applying backup... Do not power off.
            echo.

            dism.exe /Apply-Image /ImageFile:"!WIMFILE!" /Index:1 /ApplyDir:!WINVOL!\ /CheckIntegrity
            if errorlevel 1 (
              echo DISM failed.
              goto :fail
            )

            echo Updating boot...
            bcdboot.exe !WINVOL!\Windows /f UEFI

            call :DeleteFlags "!FLAGFILE!" "!WIMFILE!" WinCustoms-AutoRestore.flag
            echo Restore finished. Rebooting...
            ping -n 9 127.0.0.1 >nul
            wpeutil reboot
            exit /b 0

            :fail
            echo Auto-restore FAILED.
            echo You can run 복원-C드라이브.cmd from the WIM folder.
            goto :hold

            :hold
            echo.
            "%SYSTEMROOT%\System32\cmd.exe" /k "echo WinCustoms paused in WinRE. Type exit when finished."
            exit /b 1

            rem ===== helpers =====

            :SearchFlag
            rem %1=flag file name  %2=CAP or RES
            set "_NAME=%~1"
            set "_MODE=%~2"

            rem 1) drive roots
            for %%D in (C D E F G H I J K L M N O P Q R S T U V W Y Z) do (
              if exist "%%D:\!_NAME!" (
                if /i "!_MODE!"=="CAP" (
                  set "CAPFLAG=%%D:\!_NAME!"
                  set "CAPDRIVE=%%D:"
                ) else (
                  set "FLAGFILE=%%D:\!_NAME!"
                  set "FLAGDRIVE=%%D:"
                )
                exit /b 0
              )
            )

            rem 2) next to WIM / subfolders — skip OS volume ^(slow^), scan other drives
            for %%D in (C D E F G H I J K L M N O P Q R S T U V W Y Z) do (
              if exist "%%D:\" (
                if not exist "%%D:\Windows\System32\ntoskrnl.exe" (
                  for /f "delims=" %%F in ('dir /s /b "%%D:\!_NAME!" 2^>nul') do (
                    if /i "!_MODE!"=="CAP" (
                      set "CAPFLAG=%%F"
                      set "CAPDRIVE=%%D:"
                    ) else (
                      set "FLAGFILE=%%F"
                      set "FLAGDRIVE=%%D:"
                    )
                    exit /b 0
                  )
                )
              )
            )

            rem 3) also search OS volume shallow: one level of folders ^(WIM often on D/E but just in case^)
            for %%D in (C D E F G H I J K L M N O P Q R S T U V W Y Z) do (
              if exist "%%D:\Windows\System32\ntoskrnl.exe" (
                for /d %%P in ("%%D:\*") do (
                  if exist "%%~fP\!_NAME!" (
                    if /i "!_MODE!"=="CAP" (
                      set "CAPFLAG=%%~fP\!_NAME!"
                      set "CAPDRIVE=%%D:"
                    ) else (
                      set "FLAGFILE=%%~fP\!_NAME!"
                      set "FLAGDRIVE=%%D:"
                    )
                    exit /b 0
                  )
                )
              )
            )
            exit /b 1

            :ReadFlag
            set "WIMREL="
            set "WIMBASENAME="
            set "IMGNAME=WinCustoms Backup"
            for /f "usebackq tokens=1,* delims==" %%A in ("%~1") do (
              if /i "%%A"=="WIM" set "WIMREL=%%B"
              if /i "%%A"=="WIMFILE" set "WIMBASENAME=%%B"
              if /i "%%A"=="NAME" set "IMGNAME=%%B"
            )
            rem trim spaces
            for /f "tokens=* delims= " %%T in ("!WIMREL!") do set "WIMREL=%%T"
            for /f "tokens=* delims= " %%T in ("!WIMBASENAME!") do set "WIMBASENAME=%%T"
            if "!WIMBASENAME!"=="" if not "!WIMREL!"=="" (
              for %%I in ("!WIMREL!") do set "WIMBASENAME=%%~nxI"
            )
            exit /b 0

            :ResolveWim
            rem %1=drive like E:   %2=flag full path
            set "WIMFILE="
            set "_DRV=%~1"
            set "_FLG=%~2"

            rem a) drive root + relative WIM=
            if not "!WIMREL!"=="" (
              set "WIMFILE=!_DRV!\!WIMREL!"
              if exist "!WIMFILE!" exit /b 0
            )

            rem b) same folder as flag + WIMFILE basename
            if not "!WIMBASENAME!"=="" (
              for %%I in ("!_FLG!") do set "WIMFILE=%%~dpI!WIMBASENAME!"
              if exist "!WIMFILE!" exit /b 0
            )

            rem c) same folder as flag + WIMREL as relative name only
            if not "!WIMREL!"=="" (
              for %%I in ("!_FLG!") do set "WIMFILE=%%~dpI!WIMREL!"
              if exist "!WIMFILE!" exit /b 0
              for %%I in ("!WIMREL!") do (
                for %%J in ("!_FLG!") do set "WIMFILE=%%~dpJ%%~nxI"
              )
              if exist "!WIMFILE!" exit /b 0
            )

            set "WIMFILE="
            exit /b 1

            :FindWinVol
            set "WINVOL="
            for %%D in (C D E F G H I J K L M N O P Q R S T U V W Y Z) do (
              if exist "%%D:\Windows\System32\config\SOFTWARE" if exist "%%D:\Windows\System32\ntoskrnl.exe" (
                set "WINVOL=%%D:"
                exit /b 0
              )
            )
            exit /b 1

            :DeleteFlags
            rem %1=found flag  %2=wim path  %3=flag filename
            if exist "%~1" del /f /q "%~1" >nul 2>&1
            for %%I in ("%~2") do (
              if exist "%%~dpI%~3" del /f /q "%%~dpI%~3" >nul 2>&1
              if exist "%%~dI\%~3" del /f /q "%%~dI\%~3" >nul 2>&1
            )
            exit /b 0
            """;
}
