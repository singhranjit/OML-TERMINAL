using OmlTerminal.Core.Monitoring;

namespace OmlTerminal.Core.Tests;

public class HostStatsTests
{
    private const string LinuxSample =
        "0.10 0.05 0.01 1/234 5678\n" +
        "::OMLMEM::\n" +
        "              total        used        free      shared  buff/cache   available\n" +
        "Mem:           7822        3011         512         128        4299        4400\n" +
        "Swap:              0           0           0\n" +
        "::OMLDISK::\n" +
        "Filesystem     1M-blocks  Used Available Use% Mounted on\n" +
        "/dev/sda1          40960 12000     28000  30% /\n" +
        "::OMLUPTIME::\n" +
        " 10:32:01 up 3 days,  2:14,  2 users,  load average: 0.10, 0.05, 0.01\n";

    [Fact]
    public void ParsesLoadMemDiskAndUptimeFromLinuxOutput()
    {
        var sample = HostStats.Parse(LinuxSample);

        Assert.Equal(0.10, sample.Load1);
        Assert.Equal(0.05, sample.Load5);
        Assert.Equal(0.01, sample.Load15);
        Assert.Equal(7822, sample.MemTotalMb);
        Assert.Equal(3011, sample.MemUsedMb);
        Assert.Equal(40960, sample.DiskTotalMb);
        Assert.Equal(12000, sample.DiskUsedMb);
        Assert.Contains("up 3 days", sample.UptimeText);
        Assert.True(sample.AnyData);
    }

    [Fact]
    public void MemUsedPercentIsDerivedFromTotalAndUsed()
    {
        var sample = HostStats.Parse(LinuxSample);
        Assert.NotNull(sample.MemUsedPercent);
        Assert.InRange(sample.MemUsedPercent!.Value, 38.0, 39.0);
    }

    [Fact]
    public void UnrecognizedOutputYieldsNoData()
    {
        var sample = HostStats.Parse("% Invalid input detected at '^' marker.\n");
        Assert.False(sample.AnyData);
        Assert.Null(sample.Load1);
        Assert.Null(sample.MemTotalMb);
    }

    [Fact]
    public void EmptyOutputYieldsNoData()
    {
        var sample = HostStats.Parse("");
        Assert.False(sample.AnyData);
    }

    [Fact]
    public void ParsesDiskUsageWhenDfWrapsTheDataRowOntoTwoLines()
    {
        // A long device path (e.g. an LVM mapper name) makes "df -m /" wrap: the filesystem name gets its own
        // line and the numeric columns start on the next one.
        var sample = HostStats.Parse(
            "0.10 0.05 0.01 1/234 5678\n" +
            "::OMLMEM::\n" +
            "Mem:           7822        3011         512         128        4299        4400\n" +
            "::OMLDISK::\n" +
            "Filesystem                          1M-blocks  Used Available Use% Mounted on\n" +
            "/dev/mapper/centos-root_very_long_name\n" +
            "                                        40960 12000     28000  30% /\n" +
            "::OMLUPTIME::\n" +
            " up 3 days\n");

        Assert.Equal(40960, sample.DiskTotalMb);
        Assert.Equal(12000, sample.DiskUsedMb);
    }
}
