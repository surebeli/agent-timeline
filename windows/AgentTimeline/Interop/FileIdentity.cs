using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgentTimeline.Interop;

/// <summary>
/// Windows equivalent of the mac inode check (docs/SESSION-FORMATS.md 增量读取约定:
/// "inode / fileId 变化 → 文件被重建，offset 归零重扫").
///
/// Uses GetFileInformationByHandle to combine the volume serial number with the
/// 64-bit NTFS file index — stable across renames, changes when a file is deleted
/// and re-created, which is exactly the signal we need.
/// </summary>
public static class FileIdentity
{
    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    /// <summary>
    /// Returns "volumeSerial-fileIndex" for an open stream, or "" when the query fails
    /// (e.g. exotic filesystems) — an empty id simply disables the recreate detection.
    /// </summary>
    public static string GetFileId(FileStream stream)
    {
        try
        {
            if (GetFileInformationByHandle(stream.SafeFileHandle, out var info))
            {
                ulong index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
                return $"{info.VolumeSerialNumber:X8}-{index:X16}";
            }
        }
        catch
        {
            // fall through
        }
        return "";
    }
}
