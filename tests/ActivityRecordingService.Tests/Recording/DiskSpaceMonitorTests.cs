using ActivityRecordingService.Recording;
using FluentAssertions;

namespace ActivityRecordingService.Tests.Recording;

public sealed class DiskSpaceMonitorTests
{
    [Fact]
    public void GetSpace_ReturnsMountedPathSpace()
    {
        var directory = Directory.CreateTempSubdirectory("disk-space-");
        try
        {
            var monitor = new DiskSpaceMonitor();

            var space = monitor.GetSpace(directory.FullName);

            space.TotalBytes.Should().BeGreaterThan(0);
            space.AvailableBytes.Should().BeGreaterThan(0);
            space.AvailableBytes.Should().BeLessThanOrEqualTo(space.TotalBytes);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetSpace_RequiresAbsolutePath()
    {
        var act = () => new DiskSpaceMonitor().GetSpace("recordings");

        act.Should().Throw<ArgumentException>()
            .WithParameterName("path");
    }

    [Fact]
    public void GetSpace_ThrowsForMissingPath()
    {
        var monitor = new DiskSpaceMonitor();
        var root = Directory.CreateTempSubdirectory("disk-space-missing-");
        var missingPath = Path.Combine(root.FullName, "does-not-exist");

        try
        {
            var act = () => monitor.GetSpace(missingPath);

            act.Should().Throw<IOException>();
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
