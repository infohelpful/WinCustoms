using System.Text.Json.Serialization;

namespace WinCustoms.Common;

/// <summary>
/// 관리자 권한이 필요한 작업 묶음.
/// 비승격 인스턴스가 JSON 으로 기록하고, 'runas' 로 재실행된 자기 자신이 헤드리스로 처리한다.
/// </summary>
public sealed class ElevatedJob
{
    public List<RegistryOperation> RegistryOperations { get; set; } = [];
    public List<CommandOperation> Commands { get; set; } = [];

    public bool IsEmpty => RegistryOperations.Count == 0 && Commands.Count == 0;
}

/// <summary>승격 상태에서 실행할 외부 프로세스(powercfg, powershell 등).</summary>
public sealed class CommandOperation
{
    public string FileName { get; set; } = string.Empty;
    public List<string> Arguments { get; set; } = [];

    /// <summary>0 이 아닌 종료 코드를 실패로 취급할지 여부. 일부 CLI 는 정상 동작에도 비0 을 반환한다.</summary>
    public bool IgnoreExitCode { get; set; }

    public static CommandOperation Create(string fileName, params string[] args)
        => new() { FileName = fileName, Arguments = [.. args] };
}

public sealed class ElevatedJobResult
{
    public bool Success { get; set; }
    public List<string> Errors { get; set; } = [];
}

/// <summary>Native AOT 에서 리플렉션 없이 직렬화하기 위한 소스 생성 컨텍스트.</summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(ElevatedJob))]
[JsonSerializable(typeof(ElevatedJobResult))]
[JsonSerializable(typeof(List<CustomContextMenuEntry>))]
public sealed partial class WinCustomsJsonContext : JsonSerializerContext;

/// <summary>사용자가 우클릭 메뉴에 직접 등록한 프로그램 항목.</summary>
public sealed class CustomContextMenuEntry
{
    /// <summary>레지스트리 키 이름으로 사용되는 안전한 식별자.</summary>
    public string Key { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;

    /// <summary>대상 경로를 인수로 넘길지 여부. false 면 프로그램만 실행한다.</summary>
    public bool PassTargetPath { get; set; } = true;

    /// <summary>파일 우클릭에도 노출할지 여부. false 면 폴더/배경에만 등록한다.</summary>
    public bool ShowForFiles { get; set; } = true;

    public bool ShowForFolders { get; set; } = true;
}
