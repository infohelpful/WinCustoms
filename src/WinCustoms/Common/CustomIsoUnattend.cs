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
        || !string.IsNullOrWhiteSpace(request.LanguageOverride)
        || request.RegistryOperations.Count > 0
        || request.AutoPartitionTargetDisk;

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
        var xml = BuildXml(extractDir, request);

        var utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

        // 1. USB/ISO 루트에만 autounattend.xml 배치 (Rufus 표준 규격)
        // 소스 ISO가 우리가 예전에 만든 커스텀 ISO(이미 autounattend.xml이 루트에 있음)면,
        // robocopy가 ISO(UDF/CDFS)에서 복사하면서 ReadOnly 속성까지 그대로 가져온다.
        // 지우지 않고 바로 쓰면 UnauthorizedAccessException("Access ... is denied")이 난다.
        var rootXml = Path.Combine(extractDir, "autounattend.xml");
        CustomIsoJobHost.ClearReadOnlyAttribute(rootXml);
        File.WriteAllText(rootXml, xml, utf8Bom);

        // 대소문자 중복 파일 정리
        var rootXmlUpper = Path.Combine(extractDir, "Autounattend.xml");
        if (File.Exists(rootXmlUpper) && !string.Equals(rootXml, rootXmlUpper, StringComparison.OrdinalIgnoreCase))
        {
            CustomIsoJobHost.ClearReadOnlyAttribute(rootXmlUpper);
            try { File.Delete(rootXmlUpper); } catch { /* ignore */ }
        }

        // 2. sources\unattend.xml 및 sources\Autounattend.xml 제거
        // setup.exe가 sources\unattend.xml을 읽으면 미디어 인플레이스 업그레이드로 오인하여
        // "업그레이드를 시작하고 설치 미디어에서 부팅한 것 같습니다..." 팝업이 강제 발생합니다.
        // 이것도 ReadOnly면 삭제가 조용히 실패해서(try/catch) 옛 unattend.xml이 그대로 남아
        // 같은 팝업을 유발할 수 있으므로 삭제 전에 속성부터 지운다.
        var sourcesDir = Path.Combine(extractDir, "sources");
        if (Directory.Exists(sourcesDir))
        {
            var s1 = Path.Combine(sourcesDir, "unattend.xml");
            var s2 = Path.Combine(sourcesDir, "Autounattend.xml");
            if (File.Exists(s1)) { CustomIsoJobHost.ClearReadOnlyAttribute(s1); try { File.Delete(s1); } catch { /* ignore */ } }
            if (File.Exists(s2)) { CustomIsoJobHost.ClearReadOnlyAttribute(s2); try { File.Delete(s2); } catch { /* ignore */ } }
        }

        // sources\pid.txt 및 sources\ei.cfg 처리
        // 에디션을 선택한 경우: pid.txt (범용 키) + ei.cfg (EditionID + Retail) 함께 작성하여 메인보드 OEM 키 무시 및 에디션 즉시 자동 선택
        // 에디션을 선택하지 않은 경우: pid.txt 삭제 + ei.cfg (Retail) 로 제품키 입력창만 넘어가고 에디션 선택 목록 표출
        var editionName = (request.EditionName ?? string.Empty).Trim();
        var productKey = ResolveGenericProductKey(editionName);
        var editionId = ResolveEditionId(editionName);

        if (Directory.Exists(sourcesDir))
        {
            var pidPath = Path.Combine(sourcesDir, "pid.txt");
            var eiPath = Path.Combine(sourcesDir, "ei.cfg");
            CustomIsoJobHost.ClearReadOnlyAttribute(pidPath);
            CustomIsoJobHost.ClearReadOnlyAttribute(eiPath);

            if (!string.IsNullOrEmpty(productKey))
            {
                File.WriteAllText(pidPath, $"[PID]\r\nValue={productKey}\r\n", Encoding.ASCII);
                var eiContent = !string.IsNullOrEmpty(editionId)
                    ? $"[EditionID]\r\n{editionId}\r\n[Channel]\r\nRetail\r\n[VL]\r\n0\r\n"
                    : "[Channel]\r\nRetail\r\n[VL]\r\n0\r\n";
                File.WriteAllText(eiPath, eiContent, Encoding.ASCII);
            }
            else
            {
                if (File.Exists(pidPath))
                {
                    try { File.Delete(pidPath); } catch { /* ignore */ }
                }

                File.WriteAllText(eiPath, "[Channel]\r\nRetail\r\n[VL]\r\n0\r\n", Encoding.ASCII);
            }
        }
    }

    public static void WriteOemPantherCopy(string extractDir)
    {
        // WriteAutounattendXml 에서 통합 처리
    }

    public static List<RegistryOperation> BuildOfflineRegistryOps(string extractDir, CustomIsoJobRequest request)
    {
        var ops = new List<RegistryOperation>();

        // Default 프로필에 선호 언어(Control Panel\International\User Profile\Languages)를
        // 강제로 심어두면, 화면 언어 리소스는 이미 install.wim 에 있어도 Windows가 그 언어의
        // "추가 기능"(손글씨·음성 인식·맞춤법 검사 등)이 로컬에 없다고 보고 Windows Update로
        // 내려받으려 시도할 수 있다. 오프라인/무인 설치에서 이게 첫 로그온 직후 네트워크가
        // 불안정하면 "업데이트 확인 중"에서 무한 대기로 이어지는 게 실측 확인됐다
        // (2026-09-17). "복구 콘텐츠를 Windows Update에서 받지 않음" 정책으로 이 네트워크
        // 의존성 자체를 없앤다 — 온전한 Windows Update 자체는 막지 않는다.
        ops.Add(RegistryOperation.Set(
            RegistryRoot.LocalMachine,
            @"SOFTWARE\Policies\Microsoft\Windows\Servicing",
            "RepairContentServerSource",
            RegistryValueKind.DWord,
            0));

        if (request.SkipOnlineAccount)
        {
            ops.Add(RegistryOperation.Set(
                RegistryRoot.LocalMachine,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\OOBE",
                "BypassNRO",
                RegistryValueKind.DWord,
                1));
        }

        if (request.EnableAutoLogon || request.SkipOnlineAccount)
        {
            var rawAccount = (request.LocalAccountName ?? string.Empty).Trim();
            var account = rawAccount.Length > 0 ? rawAccount : "User";
            ops.Add(RegistryOperation.Set(
                RegistryRoot.LocalMachine,
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon",
                "AutoAdminLogon",
                RegistryValueKind.String,
                "1"));
            ops.Add(RegistryOperation.Set(
                RegistryRoot.LocalMachine,
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon",
                "DefaultUserName",
                RegistryValueKind.String,
                account));
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

        // 2026-09-18: 위 두 HKCU 값(Default 프로필 강제 주입) + FirstLogonCommands 예약
        // 작업으로 Install-Language 재실행까지 다 해봐도 Windows 보안(SecHealthUI)이 영어로
        // 뜨는 문제가 안 고쳐졌는데, 사용자가 "최적화 없이 Rufus로 구우면 정상적으로 한국어로
        // 뜬다"는 걸 실측으로 확인해줬다. 즉 이건 Microsoft 쪽 미해결 버그가 아니라, 우리가
        // "고치려고" 추가한 이 오프라인 레지스트리 주입 + FirstLogonCommands 의
        // Install-Language 재시도(BuildLangFixTaskCommand, AutoLogon 직후·OOBE 셸 초기화와
        // 겹치는 타이밍에 시스템 UI 언어를 다시 씀)가 오히려 그 초기화 과정과 충돌해서
        // SecHealthUI 리소스가 영어로 굳어버리게 만든 원인이었을 가능성이 높다. Rufus는 이
        // HKCU 강제 주입도, Install-Language 재시도도 전혀 안 하고 unattend.xml의
        // SystemLocale/UserLocale/UILanguage/UILanguageFallback/InputLocale 만 채우는데도
        // 정상 동작하므로, 여기서도 그 두 가지를 제거하고 Rufus와 동일하게 unattend.xml
        // 로케일 설정에만 의존한다. 재발하면 docs/NEXT-SESSION.md 최신 기록부터 확인할 것.

        return ops;
    }

    private static string BuildXml(string extractDir, CustomIsoJobRequest request)
    {
        var rawAccount = (request.LocalAccountName ?? string.Empty).Trim();
        // 온라인 계정 건너뛰기가 켜져 있는데 계정명을 안 쓴 경우 기본 로컬 관리자 계정 "User" 생성
        var account = rawAccount.Length > 0 ? rawAccount : (request.SkipOnlineAccount ? "User" : string.Empty);
        var hasAccount = account.Length > 0;
        var accountEsc = WebUtility.HtmlEncode(account);
        var useAutoLogon = (request.EnableAutoLogon || request.SkipOnlineAccount) && hasAccount;
        var editionName = (request.EditionName ?? string.Empty).Trim();
        var locale = ResolveLocale(extractDir, editionName, request.LanguageOverride);

        var sb = new StringBuilder(8192);
        const string compAttrs =
            """processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" """;

        var productKey = ResolveGenericProductKey(editionName);

        sb.AppendLine("""<?xml version="1.0" encoding="utf-8"?>""");
        sb.AppendLine("""<unattend xmlns="urn:schemas-microsoft-com:unattend">""");

        // 1. windowsPE — 언어/키보드 선택창, EULA 자동 동의 및 업그레이드 팝업 강제 차단
        sb.AppendLine("""  <settings pass="windowsPE">""");
        sb.AppendLine($"""    <component name="Microsoft-Windows-International-Core-WinPE" {compAttrs}>""");
        sb.AppendLine($"      <InputLocale>{locale.InputLocale}</InputLocale>");
        sb.AppendLine($"      <SystemLocale>{locale.SystemLocale}</SystemLocale>");
        sb.AppendLine($"      <UILanguage>{locale.UiLanguage}</UILanguage>");
        sb.AppendLine($"      <UserLocale>{locale.UserLocale}</UserLocale>");
        sb.AppendLine("""      <SetupUILanguage>""");
        sb.AppendLine($"        <UILanguage>{locale.UiLanguage}</UILanguage>");
        sb.AppendLine("""        <WillShowUI>Never</WillShowUI>""");
        sb.AppendLine("""      </SetupUILanguage>""");
        sb.AppendLine("""    </component>""");
        sb.AppendLine($"""    <component name="Microsoft-Windows-Setup" {compAttrs}>""");
        if (request.AutoPartitionTargetDisk)
            AppendAutoPartitionDiskConfiguration(sb, request.ImageIndex <= 0 ? 1 : request.ImageIndex);
        sb.AppendLine("""      <UserData>""");
        sb.AppendLine("""        <AcceptEula>true</AcceptEula>""");
        sb.AppendLine("""        <ProductKey>""");
        sb.AppendLine("""          <Key />""");
        sb.AppendLine("""        </ProductKey>""");
        sb.AppendLine("""      </UserData>""");
        sb.AppendLine("""      <UpgradeData>""");
        sb.AppendLine("""        <Upgrade>false</Upgrade>""");
        sb.AppendLine("""        <WillShowUI>Never</WillShowUI>""");
        sb.AppendLine("""      </UpgradeData>""");
        sb.AppendLine("""    </component>""");
        sb.AppendLine("""  </settings>""");



        // 2. specialize — BypassNRO / 개인정보 레지스트리 실행 (Rufus wue.c 1:1 규격: Order -> Path)
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
                sb.AppendLine("""        </RunSynchronousCommand>""");
            }
            sb.AppendLine("""      </RunSynchronous>""");
            sb.AppendLine("""    </component>""");
            sb.AppendLine("""  </settings>""");
        }

        // 3. oobeSystem — 언어 설정 + OOBE 건너뛰기 + 계정 자동 생성 및 자동 로그인
        // Microsoft-Windows-Shell-Setup XSD 스키마 순서: AutoLogon -> OOBE -> UserAccounts -> FirstLogonCommands
        sb.AppendLine("""  <settings pass="oobeSystem">""");
        sb.AppendLine($"""    <component name="Microsoft-Windows-International-Core" {compAttrs}>""");
        sb.AppendLine($"      <InputLocale>{locale.InputLocale}</InputLocale>");
        sb.AppendLine($"      <SystemLocale>{locale.SystemLocale}</SystemLocale>");
        sb.AppendLine($"      <UILanguage>{locale.UiLanguage}</UILanguage>");
        // UILanguageFallback 이 없으면 특정 앱(Windows 보안 등)의 리소스가 완전히
        // 스테이징 안 됐을 때 Windows 기본 폴백인 en-US 로 떨어진다(Rufus wue.c 도
        // 이 태그를 명시적으로 채워서 이 문제를 피함). 우리가 고른 언어로 폴백시켜서
        // 언어별 리소스가 일부 빠져 있어도 en-US 가 아니라 선택한 언어로 떨어지게 한다.
        sb.AppendLine($"      <UILanguageFallback>{locale.UiLanguage}</UILanguageFallback>");
        sb.AppendLine($"      <UserLocale>{locale.UserLocale}</UserLocale>");
        sb.AppendLine("""    </component>""");
        sb.AppendLine($"""    <component name="Microsoft-Windows-Shell-Setup" {compAttrs}>""");

        // 1) AutoLogon (Rufus 1:1: 빈 비밀번호일 때도 UABhAHMAcwB3AG8AcgBkAA== PlainText=false 주입 필수)
        // AutoLogon을 빼고 Rufus처럼 수동 로그인 1회로 시도해봤지만(2026-09-17) Windows 보안
        // 언어 문제는 그걸로도 안 고쳐졌고, defaultuser0 잔존/설치 중 "업데이트 확인 중" 무한
        // 대기 등 부작용만 생겨서 도로 되돌린다. Windows 보안 언어 문제는 BuildOfflineRegistryOps
        // 의 RunOnce 기반 재시도로 별도 대응한다.
        if (useAutoLogon)
        {
            var hasPwd = !string.IsNullOrEmpty(request.LocalAccountPassword);
            var pwdEsc = WebUtility.HtmlEncode(request.LocalAccountPassword ?? string.Empty);
            sb.AppendLine("""      <AutoLogon>""");
            sb.AppendLine("""        <Password>""");
            if (hasPwd)
            {
                sb.AppendLine($"          <Value>{pwdEsc}</Value>");
                sb.AppendLine("""          <PlainText>true</PlainText>""");
            }
            else
            {
                sb.AppendLine("""          <Value>UABhAHMAcwB3AG8AcgBkAA==</Value>""");
                sb.AppendLine("""          <PlainText>false</PlainText>""");
            }
            sb.AppendLine("""        </Password>""");
            sb.AppendLine("""        <Enabled>true</Enabled>""");
            sb.AppendLine("""        <LogonCount>9999999</LogonCount>""");
            sb.AppendLine($"        <Username>{accountEsc}</Username>");
            sb.AppendLine("""      </AutoLogon>""");
        }

        // 2) OOBE (Rufus 1:1 규격: Win11 튕김 원인인 HideLocalAccountScreen / HideOEMRegistrationScreens 제거)
        if (request.SkipPrivacyExperience || request.SkipOnlineAccount)
        {
            sb.AppendLine("""      <OOBE>""");
            sb.AppendLine("""        <HideEULAPage>true</HideEULAPage>""");
            sb.AppendLine("""        <HideOnlineAccountScreens>true</HideOnlineAccountScreens>""");
            sb.AppendLine("""        <HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE>""");
            sb.AppendLine("""        <ProtectYourPC>3</ProtectYourPC>""");
            sb.AppendLine("""      </OOBE>""");
        }

        // 3) UserAccounts (Rufus 1:1 규격: Password 필수 - 빈 비밀번호일 때도 UABhAHMAcwB3AG8AcgBkAA== 주입)
        if (hasAccount)
        {
            var hasPwd = !string.IsNullOrEmpty(request.LocalAccountPassword);
            var pwdEsc = WebUtility.HtmlEncode(request.LocalAccountPassword ?? string.Empty);
            sb.AppendLine("""      <UserAccounts>""");
            sb.AppendLine("""        <LocalAccounts>""");
            sb.AppendLine("""          <LocalAccount wcm:action="add">""");
            sb.AppendLine("""            <Password>""");
            if (hasPwd)
            {
                sb.AppendLine($"              <Value>{pwdEsc}</Value>");
                sb.AppendLine("""              <PlainText>true</PlainText>""");
            }
            else
            {
                sb.AppendLine("""              <Value>UABhAHMAcwB3AG8AcgBkAA==</Value>""");
                sb.AppendLine("""              <PlainText>false</PlainText>""");
            }
            sb.AppendLine("""            </Password>""");
            sb.AppendLine($"            <Description>{accountEsc}</Description>");
            sb.AppendLine($"            <DisplayName>{accountEsc}</DisplayName>");
            sb.AppendLine("""            <Group>Administrators</Group>""");
            sb.AppendLine($"            <Name>{accountEsc}</Name>");
            sb.AppendLine("""          </LocalAccount>""");
            sb.AppendLine("""        </LocalAccounts>""");
            sb.AppendLine("""      </UserAccounts>""");
        }

        // 4) FirstLogonCommands
        // Windows 보안(SecHealthUI) 영어 표시 문제 대응으로 여기서 Install-Language 재시도
        // 예약 작업(BuildLangFixTaskCommand)을 걸었었으나, 2026-09-18 실측으로 이 예약 작업
        // 자체가 AutoLogon 직후 셸 초기화와 겹쳐 문제를 오히려 유발한 것으로 판명되어 제거함
        // (BuildOfflineRegistryOps 상단 주석 참고). Rufus는 이런 후처리 없이 unattend.xml
        // 로케일 설정만으로 정상 동작한다.
        var firstLogonCommands = new List<string>();
        if (request.RegistryOperations.Count > 0)
            firstLogonCommands.Add(OemSetupScripts.FirstLogonTweaksCommand);

        if (firstLogonCommands.Count > 0)
        {
            sb.AppendLine("""      <FirstLogonCommands>""");
            for (var i = 0; i < firstLogonCommands.Count; i++)
            {
                sb.AppendLine("""        <SynchronousCommand wcm:action="add">""");
                sb.AppendLine($"          <CommandLine>{WebUtility.HtmlEncode(firstLogonCommands[i])}</CommandLine>");
                sb.AppendLine($"          <Order>{i + 1}</Order>");
                sb.AppendLine("""        </SynchronousCommand>""");
            }
            sb.AppendLine("""      </FirstLogonCommands>""");
        }

        sb.AppendLine("""    </component>""");
        sb.AppendLine("""  </settings>""");
        sb.AppendLine("""</unattend>""");
        return sb.ToString();
    }

    /// <summary>
    /// Rufus(wue.c CreateUnattendXml, UNATTEND_SILENT_INSTALL 플래그)와 동일한 구조.
    /// Setup 화면의 "새로 만들기" 휴리스틱에 맡기지 않고, 응답파일에서 직접 디스크 0을
    /// 지우고 EFI(260MB)+MSR(16MB)+주 파티션을 만들라고 명시한다. 부팅 USB 자체는
    /// DiskID=1로 잡힐 걸로 보고 그 파티션2(우리 데이터 파티션) 라벨만 바꿔 시스템이
    /// USB를 설치 대상으로 착각하지 않게 한다 — 실제로 부팅 USB가 항상 DiskID=1이 되는
    /// 보장은 없지만, Rufus도 동일하게 가정하고 동작하며 여러 디스크가 있어 애매하면
    /// 오히려 Setup이 대화형 파티션 화면을 그대로 띄운다(Rufus 주석 참고, 데이터 손실
    /// 방지 목적).
    /// </summary>
    private static void AppendAutoPartitionDiskConfiguration(StringBuilder sb, int imageIndex)
    {
        sb.AppendLine("""      <DiskConfiguration>""");
        sb.AppendLine("""        <WillShowUI>OnError</WillShowUI>""");
        sb.AppendLine("""        <Disk wcm:action="modify">""");
        sb.AppendLine("""          <DiskID>1</DiskID>""");
        sb.AppendLine("""          <ModifyPartitions>""");
        sb.AppendLine("""            <ModifyPartition wcm:action="modify">""");
        sb.AppendLine("""              <Order>1</Order>""");
        sb.AppendLine("""              <PartitionID>2</PartitionID>""");
        sb.AppendLine("""              <Label>WINCUSTOMS</Label>""");
        sb.AppendLine("""            </ModifyPartition>""");
        sb.AppendLine("""          </ModifyPartitions>""");
        sb.AppendLine("""        </Disk>""");
        sb.AppendLine("""        <Disk wcm:action="add">""");
        sb.AppendLine("""          <DiskID>0</DiskID>""");
        sb.AppendLine("""          <WillWipeDisk>true</WillWipeDisk>""");
        sb.AppendLine("""          <CreatePartitions>""");
        sb.AppendLine("""            <CreatePartition wcm:action="add">""");
        sb.AppendLine("""              <Order>1</Order>""");
        sb.AppendLine("""              <Type>EFI</Type>""");
        sb.AppendLine("""              <Size>260</Size>""");
        sb.AppendLine("""            </CreatePartition>""");
        sb.AppendLine("""            <CreatePartition wcm:action="add">""");
        sb.AppendLine("""              <Order>2</Order>""");
        sb.AppendLine("""              <Type>MSR</Type>""");
        sb.AppendLine("""              <Size>16</Size>""");
        sb.AppendLine("""            </CreatePartition>""");
        sb.AppendLine("""            <CreatePartition wcm:action="add">""");
        sb.AppendLine("""              <Order>3</Order>""");
        sb.AppendLine("""              <Type>Primary</Type>""");
        sb.AppendLine("""              <Extend>true</Extend>""");
        sb.AppendLine("""            </CreatePartition>""");
        sb.AppendLine("""          </CreatePartitions>""");
        sb.AppendLine("""          <ModifyPartitions>""");
        sb.AppendLine("""            <ModifyPartition wcm:action="add">""");
        sb.AppendLine("""              <Order>1</Order>""");
        sb.AppendLine("""              <PartitionID>1</PartitionID>""");
        sb.AppendLine("""              <Label>EFI</Label>""");
        sb.AppendLine("""              <Format>FAT32</Format>""");
        sb.AppendLine("""            </ModifyPartition>""");
        sb.AppendLine("""            <ModifyPartition wcm:action="add">""");
        sb.AppendLine("""              <Order>2</Order>""");
        sb.AppendLine("""              <PartitionID>3</PartitionID>""");
        sb.AppendLine("""              <Label>Windows</Label>""");
        sb.AppendLine("""              <Letter>C</Letter>""");
        sb.AppendLine("""              <Format>NTFS</Format>""");
        sb.AppendLine("""            </ModifyPartition>""");
        sb.AppendLine("""          </ModifyPartitions>""");
        sb.AppendLine("""        </Disk>""");
        sb.AppendLine("""      </DiskConfiguration>""");
        sb.AppendLine("""      <ImageInstall>""");
        sb.AppendLine("""        <OSImage>""");
        sb.AppendLine("""          <WillShowUI>OnError</WillShowUI>""");
        sb.AppendLine("""          <InstallFrom>""");
        sb.AppendLine("""            <MetaData wcm:action="add">""");
        sb.AppendLine("""              <Key>/IMAGE/INDEX</Key>""");
        sb.AppendLine($"              <Value>{imageIndex}</Value>");
        sb.AppendLine("""            </MetaData>""");
        sb.AppendLine("""          </InstallFrom>""");
        sb.AppendLine("""          <InstallTo>""");
        sb.AppendLine("""            <DiskID>0</DiskID>""");
        sb.AppendLine("""            <PartitionID>3</PartitionID>""");
        sb.AppendLine("""          </InstallTo>""");
        sb.AppendLine("""        </OSImage>""");
        sb.AppendLine("""      </ImageInstall>""");
    }

    private static List<string> BuildSpecializeCommands(CustomIsoJobRequest request)
    {
        var cmds = new List<string>();

        if (request.SkipOnlineAccount)
        {
            cmds.Add(@"reg add ""HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\OOBE"" /v BypassNRO /t REG_DWORD /d 1 /f");
        }

        if (request.SkipPrivacyExperience)
        {
            cmds.Add(@"reg add ""HKLM\SOFTWARE\Policies\Microsoft\Windows\OOBE"" /v DisablePrivacyExperience /t REG_DWORD /d 1 /f");
            cmds.Add(@"reg add ""HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\OOBE"" /v DisablePrivacyExperience /t REG_DWORD /d 1 /f");
        }

        return cmds;
    }

    internal sealed record LocaleSettings(
        string UiLanguage,
        string InputLocale,
        string SystemLocale,
        string UserLocale);

    internal static LocaleSettings ResolveLocale(string extractDir, string editionName, string? languageOverride)
    {
        // 사용자가 언어를 명시적으로 골랐으면(설치 언어 드롭다운) 그걸 최우선으로 쓴다.
        // 자동(기본값)일 때만 ISO 자체 언어를 감지한다.
        if (!string.IsNullOrWhiteSpace(languageOverride))
            return MapLocale(languageOverride.Trim());

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

    /// <summary>Windows 11 GVLK — 에디션 이름으로 추론(Rufus wue.c 와 동일 순서). 에디션 미선택 시 null.</summary>
    private static string? ResolveGenericProductKey(string editionName)
    {
        if (string.IsNullOrWhiteSpace(editionName))
            return null;

        var n = editionName.ToUpperInvariant();
        if (n.Contains("HOME SINGLE", StringComparison.Ordinal) || n.Contains("SINGLE LANGUAGE", StringComparison.Ordinal))
            return "7HNRX-D7KGG-3K4RQ-4WPJ4-YTDFH";
        if (n.Contains("HOME N", StringComparison.Ordinal) || n.Contains("HOMEN", StringComparison.Ordinal))
            return "325GQ-B4C3M-K83BW-MGXCC-J8PB4";
        if ((n.Contains("HOME", StringComparison.Ordinal) || n.Contains("홈", StringComparison.Ordinal)) && !n.Contains("PRO", StringComparison.Ordinal) && !n.Contains("프로", StringComparison.Ordinal))
            return "TX9XD-98N7V-6WMQ6-BX7FG-H8Q99";
        if (n.Contains("PRO N", StringComparison.Ordinal) || n.Contains("PRON", StringComparison.Ordinal))
            return "2B87N-8KFHP-DKV6R-Y2CV8-8FFHB";
        if (n.Contains("PRO", StringComparison.Ordinal) || n.Contains("프로", StringComparison.Ordinal))
            return "VK7JG-NPHTM-C97JM-3MPB6-3B69T";
        if (n.Contains("ENTERPRISE", StringComparison.Ordinal) || n.Contains("기업", StringComparison.Ordinal))
            return "XFV79-B7DJ2-R6PXH-BQCQ3-8DF43";
        if (n.Contains("EDUCATION", StringComparison.Ordinal) || n.Contains("교육", StringComparison.Ordinal))
            return "YNXW8-VP64B-4MC7Y-7Y3VX-7R9W2";

        return null;
    }

    /// <summary>ei.cfg 용 EditionID 결정 (Professional, Core, Enterprise 등).</summary>
    private static string? ResolveEditionId(string editionName)
    {
        if (string.IsNullOrWhiteSpace(editionName))
            return null;

        var n = editionName.ToUpperInvariant();
        if (n.Contains("HOME SINGLE", StringComparison.Ordinal) || n.Contains("SINGLE LANGUAGE", StringComparison.Ordinal))
            return "CoreSingleLanguage";
        if (n.Contains("HOME N", StringComparison.Ordinal) || n.Contains("HOMEN", StringComparison.Ordinal))
            return "CoreN";
        if ((n.Contains("HOME", StringComparison.Ordinal) || n.Contains("홈", StringComparison.Ordinal)) && !n.Contains("PRO", StringComparison.Ordinal) && !n.Contains("프로", StringComparison.Ordinal))
            return "Core";
        if (n.Contains("PRO N", StringComparison.Ordinal) || n.Contains("PRON", StringComparison.Ordinal))
            return "ProfessionalN";
        if (n.Contains("PRO", StringComparison.Ordinal) || n.Contains("프로", StringComparison.Ordinal))
            return "Professional";
        if (n.Contains("ENTERPRISE", StringComparison.Ordinal) || n.Contains("기업", StringComparison.Ordinal))
            return "Enterprise";
        if (n.Contains("EDUCATION", StringComparison.Ordinal) || n.Contains("교육", StringComparison.Ordinal))
            return "Education";

        return "Professional";
    }
}
