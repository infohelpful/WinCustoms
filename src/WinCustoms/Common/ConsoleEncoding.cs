using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

namespace WinCustoms.Common;

/// <summary>
/// 콘솔 도구 출력 디코딩.
/// DISM/reg 등은 보통 CP949(OEM), winget 등 최신 도구는 UTF-8 인 경우가 많다.
/// UTF-8 로만 읽거나 CP949 로만 읽으면 한글이 깨지므로 자동 판별한다.
/// </summary>
internal static class ConsoleEncoding
{
    private static readonly Lazy<Encoding> CachedOem = new(ResolveOem);
    private static readonly UTF8Encoding Utf8Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UTF8Encoding Utf8Lenient = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
    private static readonly Encoding Latin1 = Encoding.Latin1; // 바이트 보존용 (0–255)
    private static int _registered;

    public static Encoding OemOrAnsi => CachedOem.Value;

    /// <summary>StreamReader 가 바이트를 잃지 않게 Latin-1 로 받고, <see cref="DecodeAuto"/> 로 다시 디코딩할 때 쓴다.</summary>
    public static Encoding Passthrough => Latin1;

    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch
        {
            // ignore
        }

        _ = OemOrAnsi;
    }

    public static void ApplyTo(ProcessStartInfo psi)
    {
        EnsureRegistered();
        // 원시 바이트 보존 → DecodeAuto. (UTF-8/CP949 자동)
        psi.StandardOutputEncoding = Passthrough;
        psi.StandardErrorEncoding = Passthrough;
    }

    /// <summary>레거시 도구만 OEM 고정이 필요할 때.</summary>
    public static void ApplyOemTo(ProcessStartInfo psi)
    {
        EnsureRegistered();
        var enc = OemOrAnsi;
        psi.StandardOutputEncoding = enc;
        psi.StandardErrorEncoding = enc;
    }

    public static string DecodeAuto(string? latin1Preserved)
    {
        if (string.IsNullOrEmpty(latin1Preserved)) return string.Empty;
        return DecodeAuto(Latin1.GetBytes(latin1Preserved));
    }

    public static string DecodeAuto(byte[] data)
    {
        if (data.Length == 0) return string.Empty;
        EnsureRegistered();

        var offset = 0;
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            offset = 3;

        // 1) 유효한 UTF-8 이면 우선 (winget, 최신 CLI)
        try
        {
            var utf8 = Utf8Strict.GetString(data, offset, data.Length - offset);
            if (!utf8.Contains('\uFFFD'))
                return NormalizeNewlines(utf8);
        }
        catch (DecoderFallbackException)
        {
            // CP949 등
        }

        // 2) OEM/ANSI (DISM, reg, 구형 도구)
        try
        {
            return NormalizeNewlines(OemOrAnsi.GetString(data, offset, data.Length - offset));
        }
        catch
        {
            return NormalizeNewlines(Utf8Lenient.GetString(data, offset, data.Length - offset));
        }
    }

    private static string NormalizeNewlines(string s)
        => s.Replace("\0", string.Empty, StringComparison.Ordinal);

    private static Encoding ResolveOem()
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

        return Utf8Lenient;
    }

    private static IEnumerable<int> CandidateCodePages()
    {
        int oem = 0, ansi = 0;
        try { oem = CultureInfo.CurrentCulture.TextInfo.OEMCodePage; } catch { /* */ }
        try { ansi = CultureInfo.CurrentCulture.TextInfo.ANSICodePage; } catch { /* */ }

        if (oem > 0) yield return oem;
        if (ansi > 0 && ansi != oem) yield return ansi;
        yield return 949;
        yield return 936;
        yield return 932;
        yield return 850;
    }
}
