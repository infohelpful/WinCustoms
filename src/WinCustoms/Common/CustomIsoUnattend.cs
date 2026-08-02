using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WinCustoms.Common;

/// <summary>
/// 커스텀 ISO용 autounattend.xml 및 OOBE 관련 오프라인 레지스트리.
/// Win11 클린 설치에서 온라인 계정·개인정보 화면을 줄이고 로컬 계정을 미리 만든다.
/// </summary>
internal static class CustomIsoUnattend
{
    private static readonly Regex InvalidAccountChars =
        new(@"[""/\\\[\]:|;=,+\*\?<>@]", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool NeedsUnattend(CustomIsoJobRequest request) =>
        request.SkipOnlineAccount
        || request.SkipPrivacyExperience
        || !string.IsNullOrWhiteSpace(request.LocalAccountName);

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

    public static void WriteAutounattendXml(string extractDir, CustomIsoJobRequest request)
    {
        var path = Path.Combine(extractDir, "autounattend.xml");
        var xml = BuildXml(request);
        File.WriteAllText(path, xml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
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

            // Default 사용자 하이브 — 새 로컬 계정에도 적용
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

    private static string BuildXml(CustomIsoJobRequest request)
    {
        var account = (request.LocalAccountName ?? string.Empty).Trim();
        var hasAccount = account.Length > 0;
        var accountEsc = WebUtility.HtmlEncode(account);

        var hideOnline = request.SkipOnlineAccount || hasAccount ? "true" : "false";
        // ProtectYourPC=3 → 권장 설정 끄기(개인정보 관련 OOBE를 약화)
        var protect = request.SkipPrivacyExperience ? "3" : "1";

        var sb = new StringBuilder(4096);
        sb.AppendLine("""<?xml version="1.0" encoding="utf-8"?>""");
        sb.AppendLine("""<unattend xmlns="urn:schemas-microsoft-com:unattend">""");

        // windowsPE — EULA 수락 (언어는 이미지 기본값 유지, 디스크/에디션은 사용자가 선택)
        sb.AppendLine("""  <settings pass="windowsPE">""");
        sb.AppendLine("""    <component name="Microsoft-Windows-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">""");
        sb.AppendLine("""      <UserData>""");
        sb.AppendLine("""        <AcceptEula>true</AcceptEula>""");
        sb.AppendLine("""      </UserData>""");
        sb.AppendLine("""    </component>""");
        sb.AppendLine("""  </settings>""");

        sb.AppendLine("""  <settings pass="oobeSystem">""");
        sb.AppendLine("""    <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">""");
        sb.AppendLine("""      <OOBE>""");
        sb.AppendLine("""        <HideEULAPage>true</HideEULAPage>""");
        sb.AppendLine("""        <HideOEMRegistrationScreen>true</HideOEMRegistrationScreen>""");
        sb.AppendLine($"        <HideOnlineAccountScreens>{hideOnline}</HideOnlineAccountScreens>");
        sb.AppendLine("""        <HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE>""");
        sb.AppendLine($"        <ProtectYourPC>{protect}</ProtectYourPC>");
        sb.AppendLine("""      </OOBE>""");

        if (hasAccount)
        {
            sb.AppendLine("""      <UserAccounts>""");
            sb.AppendLine("""        <LocalAccounts>""");
            sb.AppendLine("""          <LocalAccount wcm:action="add">""");
            sb.AppendLine($"            <Name>{accountEsc}</Name>");
            sb.AppendLine($"            <DisplayName>{accountEsc}</DisplayName>");
            sb.AppendLine("""            <Group>Administrators</Group>""");
            sb.AppendLine("""            <Password>""");
            sb.AppendLine("""              <Value></Value>""");
            sb.AppendLine("""              <PlainText>true</PlainText>""");
            sb.AppendLine("""            </Password>""");
            sb.AppendLine("""          </LocalAccount>""");
            sb.AppendLine("""        </LocalAccounts>""");
            sb.AppendLine("""      </UserAccounts>""");
            sb.AppendLine("""      <AutoLogon>""");
            sb.AppendLine("""        <Enabled>true</Enabled>""");
            sb.AppendLine($"        <Username>{accountEsc}</Username>");
            sb.AppendLine("""        <Password>""");
            sb.AppendLine("""          <Value></Value>""");
            sb.AppendLine("""          <PlainText>true</PlainText>""");
            sb.AppendLine("""        </Password>""");
            sb.AppendLine("""        <LogonCount>1</LogonCount>""");
            sb.AppendLine("""      </AutoLogon>""");
        }

        sb.AppendLine("""    </component>""");
        sb.AppendLine("""  </settings>""");
        sb.AppendLine("""</unattend>""");
        return sb.ToString();
    }
}
