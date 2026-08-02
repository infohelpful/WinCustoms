using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace WinCustoms.Common;

/// <summary>
/// DISM / powercfg / reg / PowerShell 등 콘솔 도구 출력은
/// 한국어 Windows 에서 보통 CP949(OEM/ANSI)다. UTF-8 로 읽으면 한글이 깨진다.
/// (.NET 에서 Encoding.Default 는 UTF-8 이므로 OEM 코드 페이지를 명시해야 한다.)
/// </summary>
internal static class ConsoleEncoding
{
    private static readonly Lazy<Encoding> Cached = new(Resolve);
    private static int _registered;

    public static Encoding OemOrAnsi => Cached.Value;

    public static void EnsureRegistered()
    {
        if (System.Threading.Interlocked.Exchange(ref _registered, 1) == 1) return;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch
        {
            // ignore
        }

        // AOT 트리밍이 코드 페이지 구현을 걷어내지 않도록 한 번 참조한다.
        _ = OemOrAnsi;
    }

    public static void ApplyTo(ProcessStartInfo psi)
    {
        EnsureRegistered();
        var enc = OemOrAnsi;
        psi.StandardOutputEncoding = enc;
        psi.StandardErrorEncoding = enc;
    }

    private static Encoding Resolve()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch
        {
            // ignore
        }

        foreach (var codePage in CandidateCodePages())
        {
            try
            {
                return Encoding.GetEncoding(codePage);
            }
            catch
            {
                // try next
            }
        }

        // 최후: UTF-8 (깨질 수 있음) — GetEncoding 이 전부 실패한 경우만.
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }

    private static IEnumerable<int> CandidateCodePages()
    {
        int oem = 0, ansi = 0;
        try { oem = CultureInfo.CurrentCulture.TextInfo.OEMCodePage; } catch { /* */ }
        try { ansi = CultureInfo.CurrentCulture.TextInfo.ANSICodePage; } catch { /* */ }

        // 한국어 Windows 콘솔 도구는 보통 OEM(949) 로 출력한다.
        if (oem > 0) yield return oem;
        if (ansi > 0 && ansi != oem) yield return ansi;
        yield return 949;
        yield return 936;
        yield return 932;
        yield return 850;
    }
}
