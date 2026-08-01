using System.Diagnostics;
using WinCustoms.Common;

namespace WinCustoms.Services;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
    public string Combined => string.IsNullOrWhiteSpace(StandardError) ? StandardOutput : StandardError;
}

public interface IShellService
{
    /// <summary>탐색기를 안전하게 재시작한다(열려 있던 창은 닫힘).</summary>
    Task RestartExplorerAsync(CancellationToken ct = default);

    /// <summary>탐색기를 재시작하지 않고 셸에 설정 변경을 통지한다. 반영되는 트윅에 우선 사용한다.</summary>
    void NotifyShellSettingsChanged();

    Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, CancellationToken ct = default);

    /// <summary>PowerShell 스크립트를 Base64(UTF-16LE) 인코딩해 실행한다. 따옴표 이스케이프 문제를 피할 수 있다.</summary>
    Task<ProcessResult> RunPowerShellAsync(string script, CancellationToken ct = default);

    Task OpenUrlAsync(string url);

    /// <summary>Windows PowerShell(powershell.exe) 또는 PowerShell 7(pwsh.exe) 중 사용 가능한 실행 파일.</summary>
    string PowerShellExecutable { get; }
}

public sealed class ShellService : IShellService
{
    private static readonly Lazy<string> PowerShellPath = new(ResolvePowerShell);

    public string PowerShellExecutable => PowerShellPath.Value;

    private static string ResolvePowerShell()
    {
        // AppX 관련 cmdlet 은 Windows PowerShell 5.1 에서 가장 안정적이라 이를 우선한다.
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var windowsPowerShell = Path.Combine(system32, "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(windowsPowerShell) ? windowsPowerShell : "pwsh.exe";
    }

    public async Task RestartExplorerAsync(CancellationToken ct = default)
    {
        var explorerPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

        foreach (var process in Process.GetProcessesByName("explorer"))
        {
            using (process)
            {
                try
                {
                    process.Kill();
                    await process.WaitForExitAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // 이미 종료되었거나 접근할 수 없는 인스턴스는 건너뛴다.
                }
            }
        }

        // 셸 자동 재시작 정책이 켜져 있으면 윈도우가 알아서 띄운다. 잠시 기다렸다가 없으면 직접 실행.
        await Task.Delay(1200, ct).ConfigureAwait(false);

        if (Process.GetProcessesByName("explorer").Length == 0)
        {
            Process.Start(new ProcessStartInfo(explorerPath) { UseShellExecute = true })?.Dispose();
        }
    }

    public void NotifyShellSettingsChanged()
    {
        NativeMethods.NotifyShellAssociationChanged();
        NativeMethods.BroadcastSettingChange("Environment");
        NativeMethods.BroadcastSettingChange("Policy");
    }

    public async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException($"{fileName} 을(를) 실행할 수 없습니다.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        return new ProcessResult(process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }

    public Task<ProcessResult> RunPowerShellAsync(string script, CancellationToken ct = default)
    {
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        return RunAsync(PowerShellExecutable,
        [
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy", "Bypass",
            "-EncodedCommand", encoded
        ], ct);
    }

    public Task OpenUrlAsync(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        return Task.CompletedTask;
    }
}
