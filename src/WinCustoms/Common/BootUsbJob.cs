namespace WinCustoms.Common;

public enum BootUsbPartitionScheme
{
    Gpt = 0,
    Mbr = 1
}

public enum BootUsbFileSystem
{
    Fat32 = 0,
    Ntfs = 1
}

/// <summary>Rufus 스타일 부팅 USB 작성 요청(승격 프로세스).</summary>
public sealed class BootUsbJobRequest
{
    public string SourceIsoPath { get; set; } = string.Empty;
    public int ImageIndex { get; set; } = 1;
    public string WorkDirectory { get; set; } = string.Empty;

    public int DiskNumber { get; set; }
    public string DiskFriendlyName { get; set; } = string.Empty;
    public long DiskSizeBytes { get; set; }

    public BootUsbPartitionScheme PartitionScheme { get; set; } = BootUsbPartitionScheme.Gpt;
    public BootUsbFileSystem FileSystem { get; set; } = BootUsbFileSystem.Ntfs;
    public string VolumeLabel { get; set; } = "WIN11";
    /// <summary>0 = 기본 클러스터.</summary>
    public int ClusterSizeBytes { get; set; }
    public bool QuickFormat { get; set; } = true;
    public bool CreateExtendedLabelAndIcon { get; set; } = true;

    public List<RegistryOperation> RegistryOperations { get; set; } = [];
    public List<string> AppxPackageNames { get; set; } = [];
    public bool BypassSetupRequirements { get; set; }
    public bool InjectHostDrivers { get; set; }
    public bool SkipOnlineAccount { get; set; }
    public bool SkipPrivacyExperience { get; set; }
    public string LocalAccountName { get; set; } = string.Empty;

    public string ProgressFile { get; set; } = string.Empty;
    public string ResultFile { get; set; } = string.Empty;
    public string CancelFile { get; set; } = string.Empty;
}

public sealed class BootUsbJobResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? TargetDescription { get; set; }
}

/// <summary>UI에 표시할 USB/외장 디스크.</summary>
public sealed class BootUsbDiskInfo
{
    public int Number { get; set; }
    public string FriendlyName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string BusType { get; set; } = string.Empty;
    public string PartitionStyle { get; set; } = string.Empty;

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
        + (string.IsNullOrWhiteSpace(PartitionStyle) ? string.Empty : $" · {PartitionStyle}");
}
