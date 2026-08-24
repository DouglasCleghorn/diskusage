using DiskUsage.Core;
using System.IO;
using System.Windows.Media;

namespace diskusage.ViewModels;

public sealed class UsageItemViewModel
{
    private UsageItemViewModel(UsageNode directory, long parentSize)
    {
        Directory = directory;
        Name = directory.Name;
        FullPath = directory.FullPath;
        Size = directory.TotalSize;
        Percentage = GetPercentage(Size, parentSize);
        FileCount = directory.FileCount;
        Kind = "Folder";
        Icon = ShellIconProvider.GetFolderIcon();
    }

    private UsageItemViewModel(FileRecord file, long parentSize)
    {
        File = file;
        Name = file.Name;
        FullPath = file.FullPath;
        Size = file.Size;
        Percentage = GetPercentage(Size, parentSize);
        Kind = string.IsNullOrWhiteSpace(file.Extension) ? "File" : file.Extension.TrimStart('.').ToUpperInvariant();
        Icon = ShellIconProvider.GetFileIcon(file.Extension);
        Modified = file.ModifiedUtc.ToLocalTime();
    }

    private UsageItemViewModel(DriveInfo drive)
    {
        Drive = drive;
        FullPath = drive.RootDirectory.FullName;
        Name = string.IsNullOrWhiteSpace(drive.VolumeLabel)
            ? drive.Name
            : $"{drive.Name}  {drive.VolumeLabel}";
        Size = drive.TotalSize - drive.AvailableFreeSpace;
        Percentage = GetPercentage(Size, drive.TotalSize);
        Kind = drive.DriveType.ToString();
        Icon = ShellIconProvider.GetDriveIcon(drive.RootDirectory.FullName);
        Details = $"{SizeFormatter.Format(drive.AvailableFreeSpace)} free";
    }

    public UsageNode? Directory { get; }

    public FileRecord? File { get; }

    public DriveInfo? Drive { get; }

    public bool IsDirectory => Directory is not null || Drive is not null;

    public string Name { get; }

    public string FullPath { get; }

    public long Size { get; }

    public double Percentage { get; }

    public long? FileCount { get; }

    public DateTime? Modified { get; }

    public string? Details { get; }

    public string Kind { get; }

    public ImageSource? Icon { get; }

    public string DisplaySize => SizeFormatter.Format(Size);

    public string DisplayPercentage => $"{Percentage:0.0}%";

    public string DisplayFileCount => FileCount?.ToString("N0") ?? string.Empty;

    public string DisplayDetails => Details ?? Modified?.ToString("g") ?? string.Empty;

    public static UsageItemViewModel FromDirectory(UsageNode directory, long parentSize) => new(directory, parentSize);

    public static UsageItemViewModel FromFile(FileRecord file, long parentSize) => new(file, parentSize);

    public static UsageItemViewModel FromDrive(DriveInfo drive) => new(drive);

    private static double GetPercentage(long size, long parentSize) =>
        parentSize <= 0 ? 0 : size * 100d / parentSize;
}
