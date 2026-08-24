using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace diskusage.ViewModels;

internal static class ShellIconProvider
{
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeNormal = 0x80;
    private const uint ShgfiIcon = 0x00000100;
    private const uint ShgfiSmallIcon = 0x00000001;
    private const uint ShgfiUseFileAttributes = 0x00000010;

    private static readonly ConcurrentDictionary<string, Lazy<ImageSource?>> Icons = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? GetFolderIcon() => GetCached("folder", () => LoadIcon("folder", FileAttributeDirectory, useFileAttributes: true));

    public static ImageSource? GetDriveIcon(string rootPath) => GetCached($"drive:{rootPath}", () => LoadIcon(rootPath, 0, useFileAttributes: false));

    public static ImageSource? GetFileIcon(string extension)
    {
        var normalized = string.IsNullOrWhiteSpace(extension)
            ? string.Empty
            : extension.StartsWith('.') ? extension : $".{extension}";
        return GetCached($"file:{normalized}", () => LoadIcon($"placeholder{normalized}", FileAttributeNormal, useFileAttributes: true));
    }

    private static ImageSource? GetCached(string key, Func<ImageSource?> factory) =>
        Icons.GetOrAdd(key, _ => new Lazy<ImageSource?>(factory, LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private static ImageSource? LoadIcon(string path, uint attributes, bool useFileAttributes)
    {
        var flags = ShgfiIcon | ShgfiSmallIcon;
        if (useFileAttributes)
        {
            flags |= ShgfiUseFileAttributes;
        }

        var result = SHGetFileInfo(path, attributes, out var info, (uint)Marshal.SizeOf<SHFileInfo>(), flags);
        if (result == IntPtr.Zero || info.Icon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                info.Icon,
                Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromWidthAndHeight(16, 16));
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(info.Icon);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path,
        uint fileAttributes,
        out SHFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }
}
