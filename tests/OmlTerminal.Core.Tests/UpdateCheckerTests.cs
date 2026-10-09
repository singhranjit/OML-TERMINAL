using OmlTerminal.Core.Models;

namespace OmlTerminal.Core.Tests;

public class UpdateCheckerTests
{
    [Theory]
    [InlineData("1.0.0", "0.3.8", true)]
    [InlineData("0.3.8", "0.3.8", false)]
    [InlineData("0.3.7", "0.3.8", false)]
    [InlineData("v0.3.10", "0.3.9", true)]   // numeric, not alphabetical
    [InlineData("1.1.0-beta", "1.0.0", true)]
    [InlineData("1.0", "1.0.0.0", false)]
    [InlineData("garbage", "0.1.0", false)]
    public void Compares_versions_numerically(string published, string current, bool newer) =>
        Assert.Equal(newer, UpdateChecker.IsNewer(published, Version.Parse(current)));

    [Fact]
    public void Checks_at_most_once_a_day()
    {
        var now = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(UpdateChecker.Due(null, now));
        Assert.False(UpdateChecker.Due(now.AddHours(-3), now));
        Assert.True(UpdateChecker.Due(now.AddDays(-1), now));
    }
}
