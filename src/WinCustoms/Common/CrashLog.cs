using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;

namespace WinCustoms.Common;

/// <summary>
/// 시작 단계 예외를 파일로 남긴다.
/// WinUI 3 는 UI 스레드 콜백에서 예외가 나면 CoreMessaging 이 곧바로 fail-fast 해 버려서
/// 디버거 없이는 원인을 알 수 없기 때문에, 최소한의 흔적을 남겨 둔다.
/// </summary>
public static class CrashLog
{
    private static readonly object Gate = new();

    private static EventHandler<FirstChanceExceptionEventArgs>? _firstChance;

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinCustoms", "startup.log");

    /// <summary>
    /// 창이 뜨기 전까지만 모든 예외를 기록한다.
    /// 정상 동작 중에는 레지스트리 접근 거부 등 내부에서 처리하는 예외가 계속 발생하므로,
    /// 창이 뜬 뒤에는 <see cref="StopStartupCapture"/> 로 반드시 떼어 낸다.
    /// </summary>
    public static void BeginStartupCapture()
    {
        _firstChance = (_, e) => Write("first-chance", e.Exception);
        AppDomain.CurrentDomain.FirstChanceException += _firstChance;
    }

    public static void StopStartupCapture()
    {
        if (_firstChance is null) return;

        AppDomain.CurrentDomain.FirstChanceException -= _firstChance;
        _firstChance = null;
    }

    public static void Write(string stage, Exception exception)
    {
        var text = new StringBuilder()
            .Append(stage).Append(": ").Append(exception.GetType().FullName)
            .Append(" (HRESULT 0x").Append(exception.HResult.ToString("X8")).AppendLine(")")
            .AppendLine(exception.Message)
            .AppendLine(exception.StackTrace ?? "<no stack>");

        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
            text.Append("  inner -> ").Append(inner.GetType().FullName).Append(": ").AppendLine(inner.Message);

        Write(text.ToString());
    }

    public static void Write(string message)
    {
        Debug.WriteLine($"[WinCustoms] {message}");

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);

                var line = new StringBuilder()
                    .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                    .Append(" | ")
                    .AppendLine(message)
                    .ToString();

                File.AppendAllText(LogPath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // 로그조차 남기지 못하는 상황에서는 조용히 포기한다.
        }
    }
}
