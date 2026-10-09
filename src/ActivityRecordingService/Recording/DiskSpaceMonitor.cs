using System.Runtime.InteropServices;

namespace ActivityRecordingService.Recording;

public sealed record DiskSpaceInfo(long TotalBytes, long AvailableBytes);

public interface IDiskSpaceMonitor
{
    DiskSpaceInfo GetSpace(string path);
}

public sealed class DiskSpaceMonitor : IDiskSpaceMonitor
{
    public DiskSpaceInfo GetSpace(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path is required.", nameof(path));
        if (!Path.IsPathRooted(path))
            throw new ArgumentException("Path must be absolute.", nameof(path));

        var normalizedPath = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
            return GetWindowsDiskSpace(normalizedPath);
        if (OperatingSystem.IsMacOS())
            return GetMacOSDiskSpace(normalizedPath);
        if (OperatingSystem.IsLinux())
            return GetLinuxDiskSpace(normalizedPath);

        throw new PlatformNotSupportedException("Disk inspection is not supported on this operating system.");
    }

    private static DiskSpaceInfo GetWindowsDiskSpace(string path)
    {
        if (!GetDiskFreeSpaceExW(path, out var availableBytes, out var totalBytes, out _))
        {
            var error = Marshal.GetLastWin32Error();
            throw new IOException($"Unable to inspect disk space (Win32 error {error}).", error);
        }

        return new DiskSpaceInfo(
            ToPositiveInt64(totalBytes, "totalBytes"),
            ToPositiveInt64(availableBytes, "availableBytes"));
    }

    private static DiskSpaceInfo GetMacOSDiskSpace(string path)
    {
        if (StatVfsMac(path, out var buffer) != 0)
            ThrowLastOsError();

        return FromStatVfs(buffer.BlockSize, buffer.FragmentSize, buffer.TotalBlocks, buffer.AvailableBlocks);
    }

    private static DiskSpaceInfo GetLinuxDiskSpace(string path)
    {
        if (StatVfsLinux(path, out var buffer) != 0)
            ThrowLastOsError();

        return FromStatVfs(buffer.BlockSize, buffer.FragmentSize, buffer.TotalBlocks, buffer.AvailableBlocks);
    }

    private static DiskSpaceInfo FromStatVfs(
        ulong blockSize,
        ulong fragmentSize,
        ulong totalBlocks,
        ulong availableBlocks)
    {
        var units = fragmentSize > 0 ? fragmentSize : blockSize;
        return new DiskSpaceInfo(
            ToPositiveInt64(totalBlocks * units, "totalBytes"),
            ToPositiveInt64(availableBlocks * units, "availableBytes"));
    }

    private static long ToPositiveInt64(ulong value, string name)
    {
        if (value > long.MaxValue)
            throw new IOException($"Disk {name} is too large to report.");
        return checked((long)value);
    }

    private static void ThrowLastOsError()
    {
        var error = Marshal.GetLastWin32Error();
        throw new IOException($"Unable to inspect disk space (OS error {error}).", error);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(
        string path,
        out ulong availableBytesToCaller,
        out ulong totalBytes,
        out ulong totalFreeBytes);

    [StructLayout(LayoutKind.Sequential)]
    private struct MacStatVfs
    {
        public ulong BlockSize;
        public ulong FragmentSize;
        public uint TotalBlocks;
        public uint FreeBlocks;
        public uint AvailableBlocks;
        public uint TotalFileNodes;
        public uint FreeFileNodes;
        public uint AvailableFileNodes;
        public ulong FileSystemId;
        public ulong Flags;
        public ulong MaximumFileNameLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxStatVfs
    {
        public ulong BlockSize;
        public ulong FragmentSize;
        public ulong TotalBlocks;
        public ulong FreeBlocks;
        public ulong AvailableBlocks;
        public ulong TotalFileNodes;
        public ulong FreeFileNodes;
        public ulong AvailableFileNodes;
        public ulong FileSystemId;
        public ulong Flags;
        public ulong MaximumFileNameLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        public ulong[] Reserved;
    }

    [DllImport("libc", EntryPoint = "statvfs", SetLastError = true)]
    private static extern int StatVfsMac(string path, out MacStatVfs buffer);

    [DllImport("libc", EntryPoint = "statvfs", SetLastError = true)]
    private static extern int StatVfsLinux(string path, out LinuxStatVfs buffer);
}
