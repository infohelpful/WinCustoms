namespace WinCustoms.Common;

public enum VentoyPartitionScheme
{
    Gpt = 0,
    Mbr = 1
}

public enum VentoyFileSystem
{
    ExFat = 0,
    Ntfs = 1,
    Fat32 = 2,
    Udf = 3
}

/// <summary>Ventoy 스타일 부팅 USB 설치/업데이트 요청(승격 프로세스).</summary>
public sealed class VentoyInstallJobRequest
{
    public int DiskNumber { get; set; }
    public string DiskFriendlyName { get; set; } = string.Empty;
    public long DiskSizeBytes { get; set; }

    public VentoyPartitionScheme PartitionScheme { get; set; } = VentoyPartitionScheme.Gpt;
    public VentoyFileSystem FileSystem { get; set; } = VentoyFileSystem.ExFat;

    /// <summary>true 면 update(/U), false 면 install(/I).</summary>
    public bool IsUpdate { get; set; }

    /// <summary>true 면 기존 데이터를 유지하는 비파괴 설치(/NonDest, 파티션 리사이즈).</summary>
    public bool NonDestructive { get; set; }

    /// <summary>true 면 보안 부팅 지원 비활성화(/NoSB).</summary>
    public bool DisableSecureBoot { get; set; }

    /// <summary>0 이면 예약 공간 없음.</summary>
    public int ReserveSpaceMB { get; set; }

    public string ProgressFile { get; set; } = string.Empty;
    public string ResultFile { get; set; } = string.Empty;
    public string CancelFile { get; set; } = string.Empty;
}

public sealed class VentoyInstallJobResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }

    /// <summary>설치 후 데이터 파티션(exFAT/NTFS/FAT32)에 배정된 드라이브 문자. 실패 시 null.</summary>
    public string? DataDriveLetter { get; set; }
}

/// <summary>UI에 표시할 USB/외장 디스크(Ventoy 설치 여부 포함).</summary>
public sealed class VentoyDiskInfo
{
    public int Number { get; set; }
    public string FriendlyName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string BusType { get; set; } = string.Empty;
    public string PartitionStyle { get; set; } = string.Empty;

    /// <summary>이 디스크에 Ventoy 가 이미 설치돼 있으면 그 버전 문자열, 아니면 null.</summary>
    public string? VentoyVersion { get; set; }

    public bool IsVentoyInstalled => !string.IsNullOrWhiteSpace(VentoyVersion);

    public string SizeText
    {
        get
        {
            var gb = SizeBytes / (1024d * 1024d * 1024d);
            return gb >= 10 ? $"{gb:0} GB" : $"{gb:0.0} GB";
        }
    }

    public string DisplayText =>
        $"{FriendlyName} ({SizeText}) · {BusType}"
        + (string.IsNullOrWhiteSpace(PartitionStyle) ? string.Empty : $" · {PartitionStyle}")
        + (IsVentoyInstalled ? $" · Ventoy {VentoyVersion}" : string.Empty);
}
