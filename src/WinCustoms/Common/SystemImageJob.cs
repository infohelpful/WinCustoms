using System.Text.Json.Serialization;

namespace WinCustoms.Common;

public enum SystemImageJobKind
{
    Capture = 0,
    Apply = 1,
    /// <summary>WinRE에 자동 복원 부트스트랩을 심고 다음 부팅에 복구 환경으로 들어간다.</summary>
    PrepareAutoRestore = 2
}

/// <summary>승격 프로세스에 넘기는 시스템 이미지 작업 요청.</summary>
public sealed class SystemImageJobRequest
{
    public SystemImageJobKind Kind { get; set; }

    /// <summary>.wim 파일 전체 경로.</summary>
    public string ImageFile { get; set; } = string.Empty;

    /// <summary>캡처 시 WIM 내부 이름.</summary>
    public string ImageName { get; set; } = "WinCustoms Backup";

    /// <summary>캡처 대상 볼륨(예: C:). 보통 시스템 드라이브.</summary>
    public string CaptureVolume { get; set; } = "C:";

    /// <summary>복원 대상 경로(예: D:\).</summary>
    public string? ApplyDir { get; set; }

    public int ImageIndex { get; set; } = 1;

    public string ProgressFile { get; set; } = string.Empty;
    public string ResultFile { get; set; } = string.Empty;
    public string CancelFile { get; set; } = string.Empty;
}

public sealed class SystemImageJobResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? ImageFile { get; set; }
}

public sealed class SystemImageProgressLine
{
    public int? Percent { get; set; }
    public string Message { get; set; } = string.Empty;
    public long UtcTicks { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(ElevatedJob))]
[JsonSerializable(typeof(ElevatedJobResult))]
[JsonSerializable(typeof(List<CustomContextMenuEntry>))]
[JsonSerializable(typeof(SystemImageJobRequest))]
[JsonSerializable(typeof(SystemImageJobResult))]
[JsonSerializable(typeof(SystemImageProgressLine))]
[JsonSerializable(typeof(CustomIsoJobRequest))]
[JsonSerializable(typeof(CustomIsoJobResult))]
[JsonSerializable(typeof(List<RegistryOperation>))]
[JsonSerializable(typeof(RegistryOperation))]
public sealed partial class WinCustomsJsonContext : JsonSerializerContext;
