using System.Text.Json.Serialization;

namespace WinCustoms.Common;

/// <summary>커스텀 설치 ISO 빌드 요청(승격 프로세스).</summary>
public sealed class CustomIsoJobRequest
{
    public string SourceIsoPath { get; set; } = string.Empty;
    public string OutputIsoPath { get; set; } = string.Empty;
    public int ImageIndex { get; set; } = 1;
    public string WorkDirectory { get; set; } = string.Empty;

    public List<RegistryOperation> RegistryOperations { get; set; } = [];

    /// <summary>제거할 프로비저닝 앱 이름 후보(본명 + 별칭).</summary>
    public List<string> AppxPackageNames { get; set; } = [];

    /// <summary>TPM/Secure Boot/CPU/RAM/디스크 등 Win11 설치 검사 우회(boot.wim LabConfig + install MoSetup).</summary>
    public bool BypassSetupRequirements { get; set; }

    /// <summary>이 PC의 드라이버를 export 한 뒤 install.wim·boot.wim 에 주입.</summary>
    public bool InjectHostDrivers { get; set; }

    /// <summary>OOBE에서 Microsoft 온라인 계정 화면을 건너뛰고 로컬 계정 경로를 연다(BypassNRO 등).</summary>
    public bool SkipOnlineAccount { get; set; }

    /// <summary>OOBE 개인정보·진단·맞춤 설정 화면을 건너뛴다.</summary>
    public bool SkipPrivacyExperience { get; set; }

    /// <summary>로컬 관리자 계정 이름. 비어 있지 않으면 설치 후 해당 계정으로 바로 로그인 가능.</summary>
    public string LocalAccountName { get; set; } = string.Empty;

    public string ProgressFile { get; set; } = string.Empty;
    public string ResultFile { get; set; } = string.Empty;
    public string CancelFile { get; set; } = string.Empty;
}

public sealed class CustomIsoJobResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? OutputIsoPath { get; set; }
}

public sealed class WindowsImageInfo
{
    public int Index { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public long SizeBytes { get; set; }

    public string DisplayText => $"{Index}: {Name}";
}
