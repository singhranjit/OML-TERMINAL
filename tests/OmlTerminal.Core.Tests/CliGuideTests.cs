using OmlTerminal.Core.CliGuide;

namespace OmlTerminal.Core.Tests;

public class CliGuideTests
{
    [Fact]
    public void EveryVendorHasCommandsAndUniqueId()
    {
        Assert.NotEmpty(CliGuideLibrary.Vendors);
        Assert.All(CliGuideLibrary.Vendors, v => Assert.NotEmpty(v.Commands));
        Assert.Equal(CliGuideLibrary.Vendors.Count, CliGuideLibrary.Vendors.Select(v => v.Id).Distinct().Count());
        Assert.True(CliGuideLibrary.TotalCommands > 100, $"only {CliGuideLibrary.TotalCommands} commands");
    }

    [Fact]
    public void EveryCommandHasDescriptionAndCategory()
    {
        foreach (var v in CliGuideLibrary.Vendors)
            foreach (var c in v.Commands)
            {
                Assert.False(string.IsNullOrWhiteSpace(c.Command), $"{v.Id}: empty command");
                Assert.False(string.IsNullOrWhiteSpace(c.Description), $"{v.Id}/{c.Command}: no description");
                Assert.False(string.IsNullOrWhiteSpace(c.Category), $"{v.Id}/{c.Command}: no category");
            }
    }

    [Fact]
    public void CoversAllSupportedVendors()
    {
        foreach (var id in new[] { "cisco-ios", "cisco-nxos", "cisco-asa", "arista-eos", "juniper-junos", "fortinet-fortios", "paloalto-panos" })
            Assert.NotNull(CliGuideLibrary.Find(id));
    }

    [Fact]
    public void Search_AndsTermsAcrossAllFields()
    {
        var ios = CliGuideLibrary.Find("cisco-ios")!;
        var r = CliGuideLibrary.Search(ios, "show bgp");
        Assert.NotEmpty(r);
        Assert.All(r, c => Assert.Contains("bgp", c.Haystack));
        // "reachability" only appears in the ping description - proves description text is searchable.
        Assert.Contains(CliGuideLibrary.Search(ios, "reachability"), c => c.Command.StartsWith("ping"));
    }

    [Fact]
    public void Search_ExactPrefixRanksFirst()
    {
        var ios = CliGuideLibrary.Find("cisco-ios")!;
        var r = CliGuideLibrary.Search(ios, "show ip route");
        Assert.Equal("show ip route", r[0].Command);
    }

    [Fact]
    public void Search_BlankReturnsEverythingInVendor()
    {
        var ios = CliGuideLibrary.Find("cisco-ios")!;
        Assert.Equal(ios.Commands.Count, CliGuideLibrary.Search(ios, "").Count);
    }

    [Fact]
    public void Search_CategoryFilterLimitsResults()
    {
        var ios = CliGuideLibrary.Find("cisco-ios")!;
        var routing = CliGuideLibrary.Search(ios, "", "Routing");
        Assert.NotEmpty(routing);
        Assert.All(routing, c => Assert.Equal("Routing", c.Category));
    }

    [Fact]
    public void Search_NoMatchIsEmptyNotError()
        => Assert.Empty(CliGuideLibrary.Search(CliGuideLibrary.Find("cisco-ios")!, "zzznotacommand"));
}
