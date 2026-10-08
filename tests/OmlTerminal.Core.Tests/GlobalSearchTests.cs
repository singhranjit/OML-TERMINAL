using OmlTerminal.Core.Models;
using OmlTerminal.Core.Search;

namespace OmlTerminal.Core.Tests;

public class GlobalSearchTests
{
    private static string? Why(string line, string query) => GlobalSearch.MatchLine(line, SearchQuery.Parse(query));

    [Theory]
    [InlineData("10.20.30.40", QueryKind.Ipv4)]
    [InlineData("10.20.30.0/24", QueryKind.Network)]
    [InlineData("10.20.30.0 255.255.255.0", QueryKind.Network)]
    [InlineData("10.20.30.40/32", QueryKind.Ipv4)]
    [InlineData("0050.56a1.0001", QueryKind.Mac)]
    [InlineData("00:50:56:A1:00:01", QueryKind.Mac)]
    [InlineData("vlan 10", QueryKind.Text)]
    [InlineData("10.20.30", QueryKind.Text)]
    public void ClassifiesQueries(string text, QueryKind kind) => Assert.Equal(kind, SearchQuery.Parse(text).Kind);

    [Theory]
    [InlineData(" ip address 10.20.30.40 255.255.255.0", "exact")]
    [InlineData("neighbor 10.20.30.40 remote-as 65001", "exact")]
    [InlineData(" ip address 10.20.30.1 255.255.255.0", "in 10.20.30.0/24")]
    [InlineData("access-list 101 permit ip 10.20.30.0 0.0.0.255 any", "in 10.20.30.0/24")]
    [InlineData("ip route 10.0.0.0 255.0.0.0 192.168.1.1", "in 10.0.0.0/8")]
    [InlineData("    set subnet 10.20.30.32 255.255.255.224", "in 10.20.30.32/27")]
    [InlineData("set address WEB ip-range 10.20.30.1-10.20.30.100", "in range 10.20.30.1-10.20.30.100")]
    [InlineData("permit ip host 10.20.30.40 any", "exact")]
    [InlineData("permit ip 10.20.30.40 0.0.0.0 any", "exact")]
    public void IpQueryFindsExactAndCoveringNetworks(string line, string expected) => Assert.Equal(expected, Why(line, "10.20.30.40"));

    [Theory]
    [InlineData("ip route 0.0.0.0 0.0.0.0 10.0.0.1")]
    [InlineData(" ip address 10.20.30.4 255.255.255.252")]
    [InlineData("logging host 10.20.30.4")]
    [InlineData("version 10.20.30.40.5")]
    [InlineData("permit ip any 0.0.0.0 255.255.255.255")]
    public void IpQueryIgnoresNearMissesAndDefaultRoutes(string line) => Assert.Null(Why(line, "10.20.30.40"));

    [Theory]
    [InlineData("network 10.20.30.0 0.0.0.255 area 0", "exact")]
    [InlineData("logging host 10.20.30.77", "address inside")]
    [InlineData(" ip address 10.20.30.1 255.255.255.0", "interface inside")]
    [InlineData("ip route 10.20.0.0 255.255.0.0 Null0", "covered by 10.20.0.0/16")]
    [InlineData("ip route 10.20.30.128 255.255.255.128 10.0.0.2", "subnet 10.20.30.128/25")]
    public void NetworkQueryFindsOverlaps(string line, string expected) => Assert.Equal(expected, Why(line, "10.20.30.0/24"));

    [Theory]
    [InlineData("  10    0050.56a1.0001    DYNAMIC     Gi1/0/1")]
    [InlineData("10.0.0.1          0          00:50:56:a1:00:01 port1")]
    [InlineData("mac 00-50-56-A1-00-01")]
    public void MacQueryMatchesAnyNotation(string line) => Assert.Equal("MAC", Why(line, "0050.56A1.0001"));

    [Fact]
    public void RelevancePutsExactThenMostSpecificNetworksFirst()
    {
        var ranked = new[] { "in 10.0.0.0/8", "exact", "in 10.10.20.0/24", "in range 10.10.20.1-10.10.20.99", "covered by 10.10.0.0/16" }
            .OrderBy(GlobalSearch.Relevance).ToArray();
        Assert.Equal(new[] { "exact", "in range 10.10.20.1-10.10.20.99", "in 10.10.20.0/24", "covered by 10.10.0.0/16", "in 10.0.0.0/8" }, ranked);
    }

    [Fact]
    public void ReadsALogThatIsStillOpenForWriting()
    {
        var path = Path.Combine(Path.GetTempPath(), "oml-log-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            // Same sharing mode as SessionLogger: it writes and lets others read.
            using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            var bytes = System.Text.Encoding.UTF8.GetBytes("SW1#show ip arp\nInternet  10.20.30.40  5  0050.56a1.0001  ARPA  Vlan10\n");
            writer.Write(bytes);
            writer.Flush();

            var hits = GlobalSearch.SearchFile(new SearchFile(path, "SW1", SearchSourceKind.Log, DateTime.Now), SearchQuery.Parse("10.20.30.40"));
            Assert.Equal("SW1#show ip arp", Assert.Single(hits).Context);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TextQueryIsCaseInsensitive() => Assert.Equal("text", Why("interface Vlan10", "VLAN10"));

    [Fact]
    public void ConfigContextShowsParentSections()
    {
        var forti = new[] { "config firewall address", "    edit \"web01\"", "        set subnet 10.20.30.40 255.255.255.255", "    next", "end" };
        Assert.Equal("config firewall address › edit \"web01\"", GlobalSearch.ConfigContext(forti, 2));

        var ios = new[] { "interface Vlan10", " description users", " ip address 10.20.30.1 255.255.255.0", "!" };
        Assert.Equal("interface Vlan10", GlobalSearch.ConfigContext(ios, 2));
        Assert.Equal("", GlobalSearch.ConfigContext(ios, 0));
    }

    [Fact]
    public void LogContextIsTheCommandThatProducedTheLine()
    {
        var log = new[] { "SW1#show ip arp", "Internet  10.20.30.40   5   0050.56a1.0001  ARPA   Vlan10", "SW1#" };
        Assert.Equal("SW1#show ip arp", GlobalSearch.LogContext(log, 1));
    }

    [Fact]
    public void BackupFilesUsesOnlyTheNewestVersionPerDeviceByDefault()
    {
        var root = Path.Combine(Path.GetTempPath(), "oml-search-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "sw1"));
            File.WriteAllText(Path.Combine(root, "sw1", "sw1_2026-01-01_000000.cfg"), "old");
            File.WriteAllText(Path.Combine(root, "sw1", "sw1_2026-02-01_000000.cfg"), "new");

            var latest = GlobalSearch.BackupFiles([root], allVersions: false);
            Assert.Single(latest);
            Assert.EndsWith("2026-02-01_000000.cfg", latest[0].Path);
            Assert.Equal("sw1", latest[0].Source);
            Assert.Equal(2, GlobalSearch.BackupFiles([root, root], allVersions: true).Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void SearchLinesReportsLineNumbersAndContext()
    {
        var file = new SearchFile("x.cfg", "sw1", SearchSourceKind.Backup, DateTime.Now);
        var hits = GlobalSearch.SearchLines(["hostname sw1", "interface Vlan10", " ip address 10.20.30.1 255.255.255.0"], file, SearchQuery.Parse("10.20.30.40")).ToList();

        var hit = Assert.Single(hits);
        Assert.Equal(3, hit.LineNumber);
        Assert.Equal("interface Vlan10", hit.Context);
        Assert.Equal("in 10.20.30.0/24", hit.Match);
    }

    [Fact]
    public void SessionsMatchByHostAddressAndText()
    {
        var sessions = new[]
        {
            new SessionProfile { Name = "core1", Host = "10.20.30.40", Notes = "rack A" },
            new SessionProfile { Name = "edge", Host = "192.168.1.1", Tags = "internet" },
        };
        Assert.Single(GlobalSearch.SearchSessions(sessions, SearchQuery.Parse("10.20.30.40")));
        Assert.Single(GlobalSearch.SearchSessions(sessions, SearchQuery.Parse("10.20.0.0/16")));
        Assert.Equal("edge", Assert.Single(GlobalSearch.SearchSessions(sessions, SearchQuery.Parse("INTERNET"))).Source);
    }
}
