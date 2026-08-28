using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WinCustoms.Common;

/// <summary>
/// 커스텀 ISO/USB용 autounattend.xml 및 OOBE 관련 오프라인 레지스트리.
/// Win11 25H2: Rufus 와 동일하게 windowsPE(라이선스·에디션·키·언어·업데이트) +
/// specialize(BypassNRO) + oobeSystem 을 구성한다. 디스크 파티션은 사용자가 수동 선택.
/// </summary>
internal static class CustomIsoUnattend
{
    private static readonly Regex InvalidAccountChars =
        new(@"[""/\\\[\]:|;=,+\*\?<>@]", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool NeedsUnattend(CustomIsoJobRequest request) =>
        request.SkipOnlineAccount
        || request.SkipPrivacyExperience
        || !string.IsNullOrWhiteSpace(request.LocalAccountName)
        || request.EnableAutoLogon
        || !string.IsNullOrWhiteSpace(request.EditionName)
        || request.RegistryOperations.Count > 0;

    /// <summary>Windows 로컬 계정 이름 규칙. 통과하면 null, 실패하면 한글 오류 메시지.</summary>
    public static string? ValidateAccountName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var n = name.Trim();
        if (n.Length is < 1 or > 20)
            return "계정 이름은 1~20자여야 합니다.";
        if (n.EndsWith('.'))
            return "계정 이름은 마침표로 끝날 수 없습니다.";
        if (InvalidAccountChars.IsMatch(n))
            return "계정 이름에 사용할 수 없는 문자가 있습니다.";
        if (n.Equals(".", StringComparison.Ordinal) || n.Equals("..", StringComparison.Ordinal))
            return "계정 이름이 올바르지 않습니다.";

        return null;
    }

    /// <summary>AutoLogon 사용 시 계정·비밀번호 검사. 통과하면 null.</summary>
    public static string? ValidateAutoLogon(string? accountName, bool enableAutoLogon, string? password)
    {
        if (!enableAutoLogon)
            return null;

        var name = (accountName ?? string.Empty).Trim();
        if (name.Length == 0)
            return "자동 로그인을 쓰려면 로컬 계정 이름을 입력하세요.";

        var accountError = ValidateAccountName(name);
        if (accountError is not null)
            return accountError;

        var pwd = password ?? string.Empty;
        if (pwd.Length == 0)
            return "자동 로그인을 쓰려면 비밀번호를 입력하세요.";
        if (pwd.Length > 127)
            return "비밀번호는 127자 이하여야 합니다.";

        return null;
    }

    public static void WriteAutounattendXml(string extractDir, CustomIsoJobRequest request)
    {
        var path = Path.Combine(extractDir, "autounattend.xml");
        var xml = BuildXml(extractDir, request);
        File.WriteAllText(path, xml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        // sources\pid.txt 및 sources\ei.cfg 자동 생성
        // Windows 설치 마법사가 부팅 시 제품키/에디션 선택 화면을 100% 무조건 건너뛰도록 강제
        var editionName = (request.EditionName ?? string.Empty).Trim();
        var productKey = ResolveGenericProductKey(editionName);
        var sourcesDir = Path.Combine(extractDir, "sources");
        if (Directory.Exists(sourcesDir))
        {
            if (!string.IsNullOrEmpty(productKey))
            {
                var pidPath = Path.Combine(sourcesDir, "pid.txt");
                var pidContent = $"[PID]\r\nValue={productKey}\r\n";
                File.WriteAllText(pidPath, pidContent, Encoding.ASCII);
            }

            var eiPath = Path.Combine(sourcesDir, "ei.cfg");
            var eiContent = "[Channel]\r\n_Default\r\n[VL]\r\n0\r\n";
            File.WriteAllText(eiPath, eiContent, Encoding.ASCII);
        }
    }

    /// <summary>
    /// Rufus: windowsPE 가 없을 때 $OEM$\$$\Panther\unattend.xml 백업 경로.
    /// windowsPE 가 있어도 이중 안전을 위해 함께 쓴다.
    /// </summary>
    public static void WriteOemPantherCopy(string extractDir)
    {
        var src = Path.Combine(extractDir, "autounattend.xml");
        if (!File.Exists(src)) return;

        var dest = Path.Combine(extractDir, "sources", "$OEM$", "$$", "Panther", "unattend.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(src, dest, overwrite: true);
    }

    public static List<RegistryOperation> BuildOfflineRegistryOps(CustomIsoJobRequest request)
    {
        var ops = new List<RegistryOperation>();

        if (request.SkipOnlineAccount)
        {
            ops.Add(RegistryOperation.Set(
                RegistryRoot.LocalMachine,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\OOBE",
                "BypassNRO",
                RegistryValueKind.DWord,
                1));
        }

        if (request.SkipPrivacyExperience)
        {
            ops.Add(RegistryOperation.Set(
                RegistryRoot.LocalMachine,
                @"SOFTWARE\Policies\Microsoft\Windows\OOBE",
                "DisablePrivacyExperience",
                RegistryValueKind.DWord,
                1));
            ops.Add(RegistryOperation.Set(
                RegistryRoot.LocalMachine,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\OOBE",
                "DisablePrivacyExperience",
                RegistryValueKind.DWord,
                1));

            ops.Add(RegistryOperation.Set(
                RegistryRoot.CurrentUser,
                @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo",
                "Enabled",
                RegistryValueKind.DWord,
                0));
            ops.Add(RegistryOperation.Set(
                RegistryRoot.CurrentUser,
                @"Software\Microsoft\Windows\CurrentVersion\Privacy",
                "TailoredExperiencesWithDiagnosticDataEnabled",
                RegistryValueKind.DWord,
                0));
            ops.Add(RegistryOperation.Set(
                RegistryRoot.CurrentUser,
                @"Software\Microsoft\Input\TIPC",
                "Enabled",
                RegistryValueKind.DWord,
                0));
            ops.Add(RegistryOperation.Set(
                RegistryRoot.CurrentUser,
                @"Software\Microsoft\Siuf\Rules",
                "NumberOfSIUFInPeriod",
                RegistryValueKind.DWord,
                0));
        }

        return ops;
    }

    private static string BuildXml(string extractDir, CustomIsoJobRequest request)
    {
        var account = (request.LocalAccountName ?? string.Empty).Trim();
        var hasAccount = account.Length > 0;
        var accountEsc = WebUtility.HtmlEncode(account);
        var useAutoLogon = request.EnableAutoLogon && hasAccount;
        var editionName = (request.EditionName ?? string.Empty).Trim();
        var locale = ResolveLocale(extractDir, editionName);
        var productKey = ResolveGenericProductKey(editionName);

        var sb = new StringBuilder(8192);
        const string compAttrs =
            """processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" """;

        sb.AppendLine("""<?xml version="1.0" encoding="utf-8"?>""");
        sb.AppendLine("""<unattend xmlns="urn:schemas-microsoft-com:unattend">""");

        // windowsPE — 언어·EULA(약관)·제품키·에디션 자동 통과. ImageInstall(자동 파티션)을 제외하여 순정 파티션 마법사로 직행.
        sb.AppendLine("""  <settings pass="windowsPE">""");
        sb.AppendLine($"""    <component name="Microsoft-Windows-International-Core-WinPE" {compAttrs}>""");
        sb.AppendLine("""      <SetupUILanguage>""");
        sb.AppendLine($"        <UILanguage>{locale.UiLanguage}</UILanguage>");
        sb.AppendLine("""      </SetupUILanguage>""");
        sb.AppendLine($"      <InputLocale>{locale.InputLocale}</InputLocale>");
        sb.AppendLine($"      <SystemLocale>{locale.SystemLocale}</SystemLocale>");
        sb.AppendLine($"      <UILanguage>{locale.UiLanguage}</UILanguage>");
        sb.AppendLine($"      <UserLocale>{locale.UserLocale}</UserLocale>");
        sb.AppendLine("""    </component>""");
        sb.AppendLine($"""    <component name="Microsoft-Windows-Setup" {compAttrs}>""");
        sb.AppendLine("""      <UserData>""");
        sb.AppendLine("""        <AcceptEula>true</AcceptEula>""");
        if (!string.IsNullOrEmpty(productKey))
        {
            sb.AppendLine("""        <ProductKey>""");
            sb.AppendLine($"          <Key>{productKey}</Key>");
            sb.AppendLine("""          <WillShowUI>OnError</WillShowUI>""");
            sb.AppendLine("""        </ProductKey>""");
        }
        sb.AppendLine("""      </UserData>""");
        sb.AppendLine("""      <DynamicUpdate>""");
        sb.AppendLine("""        <Enable>false</Enable>""");
        sb.AppendLine("""        <WillShowUI>Never</WillShowUI>""");
        sb.AppendLine("""      </DynamicUpdate>""");
        sb.AppendLine("""    </component>""");
        sb.AppendLine("""  </settings>""");

        // specialize — 설치 중 BypassNRO / 개인정보 레지스트리를 직접 실행 (25H2 대응).
        var syncCommands = BuildSpecializeCommands(request);
        if (syncCommands.Count > 0)
        {
            sb.AppendLine("""  <settings pass="specialize">""");
            sb.AppendLine($"""    <component name="Microsoft-Windows-Deployment" {compAttrs}>""");
            sb.AppendLine("""      <RunSynchronous>""");
            for (var i = 0; i < syncCommands.Count; i++)
            {
                sb.AppendLine("""        <RunSynchronousCommand wcm:action="add">""");
                sb.AppendLine($"          <Order>{i + 1}</Order>");
                sb.AppendLine($"          <Path>{WebUtility.HtmlEncode(syncCommands[i])}</Path>");
                sb.AppendLine("""          <Description>WinCustoms OOBE</Description>""");
                sb.AppendLine("""        </RunSynchronousCommand>""");
            }
            sb.AppendLine("""      </RunSynchronous>""");
            sb.AppendLine("""    </component>""");
            sb.AppendLine("""  </settings>""");
        }

        // oobeSystem — 로컬 계정 생성, 자동 로그인, 개인정보/계정 OOBE 건너뛰기, 설치 후 트윅 적용.
        sb.AppendLine("""  <settings pass="oobeSystem">""");
        sb.AppendLine($"""    <component name="Microsoft-Windows-International-Core" {compAttrs}>""");
        sb.AppendLine($"      <InputLocale>{locale.InputLocale}</InputLocale>");
        sb.AppendLine($"      <SystemLocale>{locale.SystemLocale}</SystemLocale>");
        sb.AppendLine($"      <UILanguage>{locale.UiLanguage}</UILanguage>");
        sb.AppendLine($"      <UserLocale>{locale.UserLocale}</UserLocale>");
        sb.AppendLine("""    </component>""");
        sb.AppendLine($"""    <component name="Microsoft-Windows-Shell-Setup" {compAttrs}>""");

        if (request.SkipPrivacyExperience || request.SkipOnlineAccount)
        {
            sb.AppendLine("""      <OOBE>""");
            if (request.SkipPrivacyExperience)
            {
                sb.AppendLine("""        <HideEULAPage>true</HideEULAPage>""");
                sb.AppendLine("""        <ProtectYourPC>3</ProtectYourPC>""");
            }
            if (request.SkipOnlineAccount)
            {
                sb.AppendLine("""        <HideOnlineAccountScreens>true</HideOnlineAccountScreens>""");
                sb.AppendLine("""        <HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE>""");
            }
            sb.AppendLine("""      </OOBE>""");
        }

        if (hasAccount)
        {
            var pwdEsc = WebUtility.HtmlEncode(request.LocalAccountPassword ?? string.Empty);
            if (useAutoLogon)
            {
                sb.AppendLine("""      <AutoLogon>""");
                sb.AppendLine("""        <Password>""");
                sb.AppendLine($"          <Value>{pwdEsc}</Value>");
                sb.AppendLine("""          <PlainText>true</PlainText>""");
                sb.AppendLine("""        </Password>""");
                sb.AppendLine("""        <Enabled>true</Enabled>""");
                sb.AppendLine("""        <LogonCount>9999999</LogonCount>""");
                sb.AppendLine($"        <Username>{accountEsc}</Username>");
                sb.AppendLine("""      </AutoLogon>""");
            }

            sb.AppendLine("""      <UserAccounts>""");
            sb.AppendLine("""        <LocalAccounts>""");
            sb.AppendLine("""          <LocalAccount wcm:action="add">""");
            sb.AppendLine($"            <Name>{accountEsc}</Name>");
            sb.AppendLine($"            <DisplayName>{accountEsc}</DisplayName>");
            sb.AppendLine("""            <Group>Administrators</Group>""");
            sb.AppendLine("""            <Password>""");
            sb.AppendLine($"              <Value>{pwdEsc}</Value>");
            sb.AppendLine("""              <PlainText>true</PlainText>""");
            sb.AppendLine("""            </Password>""");
            sb.AppendLine("""          </LocalAccount>""");
            sb.AppendLine("""        </LocalAccounts>""");
            sb.AppendLine("""      </UserAccounts>""");

            if (request.RegistryOperations.Count > 0)
            {
                sb.AppendLine("""      <FirstLogonCommands>""");
                AppendFirstLogonTweakCommand(sb, order: 1, request);
                sb.AppendLine("""      </FirstLogonCommands>""");
            }
        }
        else if (request.RegistryOperations.Count > 0)
        {
            sb.AppendLine("""      <FirstLogonCommands>""");
            AppendFirstLogonTweakCommand(sb, order: 1, request);
            sb.AppendLine("""      </FirstLogonCommands>""");
        }

        sb.AppendLine("""    </component>""");
        sb.AppendLine("""  </settings>""");
        sb.AppendLine("""</unattend>""");
        return sb.ToString();
    }

    private static void AppendFirstLogonTweakCommand(StringBuilder sb, int order, CustomIsoJobRequest request)
    {
        if (request.RegistryOperations.Count == 0)
            return;

        sb.AppendLine("""        <SynchronousCommand wcm:action="add">""");
        sb.AppendLine($"          <Order>{order}</Order>");
        sb.AppendLine($"          <CommandLine>{WebUtility.HtmlEncode(OemSetupScripts.FirstLogonTweaksCommand)}</CommandLine>");
        sb.AppendLine("""          <Description>WinCustoms registry tweaks</Description>""");
        sb.AppendLine("""        </SynchronousCommand>""");
    }

    private static List<string> BuildSpecializeCommands(CustomIsoJobRequest request)
    {
        var cmds = new List<string>();

        if (request.SkipOnlineAccount)
        {
            cmds.Add(
                "reg add \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\OOBE\" "
                + "/v BypassNRO /t REG_DWORD /d 1 /f");
        }

        if (request.SkipPrivacyExperience)
        {
            cmds.Add(
                "reg add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\OOBE\" "
                + "/v DisablePrivacyExperience /t REG_DWORD /d 1 /f");
            cmds.Add(
                "reg add \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\OOBE\" "
                + "/v DisablePrivacyExperience /t REG_DWORD /d 1 /f");
        }

        return cmds;
    }

    private sealed record LocaleSettings(
        string UiLanguage,
        string InputLocale,
        string SystemLocale,
        string UserLocale);

    private static LocaleSettings ResolveLocale(string extractDir, string editionName)
    {
        var lang = DetectUiLanguage(extractDir) 
                   ?? GuessLanguageFromEdition(editionName)
                   ?? System.Globalization.CultureInfo.CurrentUICulture.Name;
        return MapLocale(lang);
    }

    private static string? DetectUiLanguage(string extractDir)
    {
        // 1. sources\lang.ini 확인 (가장 빠르고 정확함)
        try
        {
            var langIni = Path.Combine(extractDir, "sources", "lang.ini");
            if (File.Exists(langIni))
            {
                var lines = File.ReadAllLines(langIni);
                var inSection = false;
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("[Available UI Languages]", StringComparison.OrdinalIgnoreCase))
                    {
                        inSection = true;
                        continue;
                    }
                    if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                    {
                        inSection = false;
                        continue;
                    }
                    if (inSection && trimmed.Contains('='))
                    {
                        var parts = trimmed.Split('=');
                        var langCode = parts[0].Trim();
                        if (langCode.Length >= 2)
                            return langCode;
                    }
                }
            }
        }
        catch { /* ignore */ }

        // 2. boot.wim DISM 검사
        var bootWim = Path.Combine(extractDir, "sources", "boot.wim");
        if (!File.Exists(bootWim))
            return null;

        var dism = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dism.exe");
        if (!File.Exists(dism))
            return null;

        for (var index = 1; index <= 4; index++)
        {
            var psi = new ProcessStartInfo
            {
                FileName = dism,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            ConsoleEncoding.ApplyOemTo(psi);
            psi.ArgumentList.Add("/Get-WimInfo");
            psi.ArgumentList.Add($"/WimFile:{bootWim}");
            psi.ArgumentList.Add($"/Index:{index}");

            using var p = Process.Start(psi);
            if (p is null)
                continue;

            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(30_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* */ }
                continue;
            }

            var output = ConsoleEncoding.DecodeAuto(stdoutTask.GetAwaiter().GetResult())
                         + ConsoleEncoding.DecodeAuto(stderrTask.GetAwaiter().GetResult());
            if (p.ExitCode != 0)
                continue;

            foreach (var rawLine in output.Split('\n'))
            {
                var line = rawLine.Trim();
                if (!line.Contains("Default Language", StringComparison.OrdinalIgnoreCase)
                    && !line.Contains("기본 언어", StringComparison.OrdinalIgnoreCase)
                    && !line.Contains("Language", StringComparison.OrdinalIgnoreCase)
                    && !line.Contains("언어", StringComparison.OrdinalIgnoreCase))
                    continue;

                var colon = line.IndexOf(':');
                if (colon < 0 || colon >= line.Length - 1)
                    continue;

                var lang = line[(colon + 1)..].Trim();
                if (lang.Length >= 2 && !lang.Contains(' ') && !lang.Contains(':'))
                    return lang;
            }
        }

        return null;
    }

    private static string? GuessLanguageFromEdition(string editionName)
    {
        if (string.IsNullOrWhiteSpace(editionName))
            return null;

        if (editionName.Contains("Korean", StringComparison.OrdinalIgnoreCase)
            || editionName.Contains("한국", StringComparison.OrdinalIgnoreCase)
            || editionName.Contains("대한민국", StringComparison.OrdinalIgnoreCase)
            || editionName.Contains("ko-KR", StringComparison.OrdinalIgnoreCase))
            return "ko-KR";

        if (editionName.Contains("English", StringComparison.OrdinalIgnoreCase)
            || editionName.Contains("en-US", StringComparison.OrdinalIgnoreCase))
            return "en-US";

        return null;
    }

    private static LocaleSettings MapLocale(string lang)
    {
        var normalized = lang.Replace('_', '-');
        if (normalized.StartsWith("ko", StringComparison.OrdinalIgnoreCase))
            return new LocaleSettings("ko-KR", "0412:00000412", "ko-KR", "ko-KR");

        if (normalized.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
            return new LocaleSettings("ja-JP", "0411:00000411", "ja-JP", "ja-JP");

        if (normalized.StartsWith("zh-CN", StringComparison.OrdinalIgnoreCase) || normalized.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase))
            return new LocaleSettings("zh-CN", "0804:00000804", "zh-CN", "zh-CN");

        if (normalized.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase) || normalized.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase))
            return new LocaleSettings("zh-TW", "0404:00000404", "zh-TW", "zh-TW");

        return new LocaleSettings("en-US", "0409:00000409", "en-US", "en-US");
    }

    /// <summary>Windows 11 GVLK — 에디션 이름으로 추론(Rufus wue.c 와 동일 순서).</summary>
    private static string ResolveGenericProductKey(string editionName)
    {
        if (string.IsNullOrWhiteSpace(editionName))
            return "VK7JG-NPHTM-C97JM-3MPB6-3B69T";

        var n = editionName.ToUpperInvariant();
        if (n.Contains("HOME SINGLE", StringComparison.Ordinal) || n.Contains("SINGLE LANGUAGE", StringComparison.Ordinal))
            return "7HNRX-D7KGG-3K4RQ-4WPJ4-YTDFH";
        if (n.Contains("HOME", StringComparison.Ordinal) && !n.Contains("PRO", StringComparison.Ordinal))
            return "TX9XD-98N7V-6WMQ6-BX7FG-H8Q99";
        if (n.Contains("PRO N", StringComparison.Ordinal) || n.Contains("PRON", StringComparison.Ordinal))
            return "2B87N-8KFHP-DKV6R-Y2CV8-8FFHB";
        if (n.Contains("PRO", StringComparison.Ordinal))
            return "VK7JG-NPHTM-C97JM-3MPB6-3B69T";
        if (n.Contains("ENTERPRISE", StringComparison.Ordinal))
            return "XFV79-B7DJ2-R6PXH-BQCQ3-8DF43";
        if (n.Contains("EDUCATION", StringComparison.Ordinal))
            return "YNXW8-VP64B-4MC7Y-7Y3VX-7R9W2";

        return "VK7JG-NPHTM-C97JM-3MPB6-3B69T";
    }
}
