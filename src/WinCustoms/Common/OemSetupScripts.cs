using System.Text;

namespace WinCustoms.Common;

/// <summary>$OEM$\$$\Setup\Scripts — 설치 완료/첫 로그온 후처리 스크립트.</summary>
internal static class OemSetupScripts
{
    public static void Write(string extractDir, IReadOnlyList<string> appxNames, IReadOnlyList<RegistryOperation> registryOps)
    {
        var hasAppx = appxNames.Count > 0;
        var hasTweaks = registryOps.Count > 0;
        if (!hasAppx && !hasTweaks)
            return;

        var scriptDir = Path.Combine(extractDir, "sources", "$OEM$", "$$", "Setup", "Scripts");
        Directory.CreateDirectory(scriptDir);

        if (hasAppx)
            ProvisionedAppxRemover.WriteDebloatScript(scriptDir, appxNames);

        if (hasTweaks)
        {
            var ps = RegistryTweakScriptWriter.BuildPowerShell(registryOps);
            File.WriteAllText(
                Path.Combine(scriptDir, "WinCustoms-ApplyTweaks.ps1"),
                ps,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }

        var cmd = new StringBuilder();
        cmd.AppendLine("@echo off");
        if (hasAppx)
            cmd.AppendLine("powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"%~dp0WinCustoms-RemoveApps.ps1\"");
        if (hasTweaks)
            cmd.AppendLine("powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"%~dp0WinCustoms-ApplyTweaks.ps1\" -Mode SetupComplete");
        cmd.AppendLine("exit /b 0");

        File.WriteAllText(Path.Combine(scriptDir, "SetupComplete.cmd"), cmd.ToString(), Encoding.ASCII);
    }

    public const string FirstLogonTweaksCommand =
        "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"%WINDIR%\\Setup\\Scripts\\WinCustoms-ApplyTweaks.ps1\" -Mode FirstLogon";
}
