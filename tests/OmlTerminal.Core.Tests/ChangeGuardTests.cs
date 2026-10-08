using OmlTerminal.Core.ChangeGuard;
using OmlTerminal.Core.Ssh;

namespace OmlTerminal.Core.Tests;

public class ChangeGuardTests
{
    private const string BriefBefore =
        "Interface              IP-Address      OK? Method Status                Protocol\n" +
        "GigabitEthernet0/0     10.0.0.2        YES NVRAM  up                    up      \n" +
        "GigabitEthernet0/1     10.0.1.2        YES NVRAM  up                    up      \n" +
        "GigabitEthernet0/2     unassigned      YES unset  administratively down down    \n";

    private const string BriefAfter =
        "Interface              IP-Address      OK? Method Status                Protocol\n" +
        "GigabitEthernet0/0     10.0.0.2        YES NVRAM  up                    up      \n" +
        "GigabitEthernet0/1     10.0.1.2        YES NVRAM  up                    down    \n" +
        "GigabitEthernet0/2     unassigned      YES unset  up                    up      \n";

    [Fact]
    public void InterfaceGoingDownIsCriticalAndComingUpIsInfo()
    {
        var f = GuardCompare.CompareOutputs("Interfaces", CheckKind.Interfaces, BriefBefore, BriefAfter);

        var down = Assert.Single(f, x => x.Severity == GuardSeverity.Critical);
        Assert.Contains("GigabitEthernet0/1", down.Message);
        Assert.Contains("up/up → up/down", down.Message);
        Assert.Contains(f, x => x.Severity == GuardSeverity.Info && x.Message.Contains("GigabitEthernet0/2"));
    }

    [Fact]
    public void IdenticalOutputReportsNoChange()
    {
        var f = Assert.Single(GuardCompare.CompareOutputs("Interfaces", CheckKind.Interfaces, BriefBefore, BriefBefore));
        Assert.Equal(GuardSeverity.Info, f.Severity);
        Assert.StartsWith("No change", f.Message);
    }

    private const string RoutesBefore =
        "S*    0.0.0.0/0 [1/0] via 10.0.0.1\n" +
        "C        10.0.0.0/30 is directly connected, GigabitEthernet0/0\n" +
        "O        10.2.0.0/16 [110/20] via 10.0.0.1, 00:01:02, GigabitEthernet0/0\n" +
        "O        10.3.0.0/16 [110/20] via 10.0.0.1, 00:01:02, GigabitEthernet0/0\n";

    [Fact]
    public void LostDefaultRouteIsCriticalAndReroutesAreWarnings()
    {
        var after =
            "C        10.0.0.0/30 is directly connected, GigabitEthernet0/0\n" +
            "O        10.2.0.0/16 [110/30] via 10.0.1.1, 00:00:05, GigabitEthernet0/1\n" +
            "O        10.3.0.0/16 [110/20] via 10.0.0.1, 00:09:59, GigabitEthernet0/0\n" +
            "O        10.4.0.0/16 [110/20] via 10.0.0.1, 00:00:05, GigabitEthernet0/0\n";

        var f = GuardCompare.CompareOutputs("Routing table", CheckKind.Routes, RoutesBefore, after);

        Assert.Contains(f, x => x.Severity == GuardSeverity.Critical && x.Message.StartsWith("Default route lost"));
        var rerouted = Assert.Single(f, x => x.Message.Contains("different path"));
        Assert.Equal(GuardSeverity.Warning, rerouted.Severity);
        Assert.Contains(rerouted.Details!, d => d.StartsWith("10.2.0.0/16") && d.Contains("10.0.1.1"));
        Assert.DoesNotContain(rerouted.Details!, d => d.StartsWith("10.3.0.0/16")); // only the age changed
        Assert.Contains(f, x => x.Message == "1 new route(s)");
    }

    [Fact]
    public void RouteCodesDropTheCandidateDefaultMarker()
    {
        var map = GuardCompare.RouteMap("O*E2  0.0.0.0/0 [110/1] via 10.0.0.1, 3w2d, Gi0/0\nS*    10.9.0.0/16 [1/0] via 10.0.0.9\n");
        Assert.Equal("O E2 via 10.0.0.1", map["0.0.0.0/0"]);
        Assert.Equal("S via 10.0.0.9", map["10.9.0.0/16"]);
    }

    [Fact]
    public void IosBuildingConfigurationBannerIsNotAConfigChange() =>
        Assert.True(Backup.ConfigBackup.IsVolatile("Building configuration..."));

    [Fact]
    public void RouteAgesAloneAreNotAChange()
    {
        var aged = RoutesBefore.Replace("00:01:02", "02:00:00");
        var f = GuardCompare.CompareOutputs("Routing table", CheckKind.Routes, RoutesBefore, aged);
        Assert.Equal("No change (4 prefixes)", Assert.Single(f).Message);
    }

    private const string BgpHeader = "Neighbor        V           AS MsgRcvd MsgSent   TblVer  InQ OutQ Up/Down  State/PfxRcd\n";

    [Fact]
    public void BgpPeerDroppingIsCriticalAndPrefixLossIsWarning()
    {
        var before = BgpHeader +
            "10.0.0.2        4        65002    1234    1230      100    0    0 1w2d          120\n" +
            "10.0.0.6        4        65003    1234    1230      100    0    0 1w2d          100\n";
        var after = BgpHeader +
            "10.0.0.2        4        65002    1234    1230      100    0    0 00:00:12 Active\n" +
            "10.0.0.6        4        65003    1234    1230      100    0    0 1w2d           40\n";

        var f = GuardCompare.CompareOutputs("BGP peers", CheckKind.BgpPeers, before, after);

        Assert.Contains(f, x => x.Severity == GuardSeverity.Critical && x.Message == "BGP neighbor 10.0.0.2 went down: Established → Active");
        Assert.Contains(f, x => x.Severity == GuardSeverity.Warning && x.Message == "BGP neighbor 10.0.0.6: prefixes 100 → 40");
    }

    [Fact]
    public void LostOspfAdjacencyIsCritical()
    {
        const string header = "Neighbor ID     Pri   State           Dead Time   Address         Interface\n";
        var before = header + "10.0.0.2          1   FULL/DR         00:00:38    10.1.1.2        GigabitEthernet0/0\n";
        var f = GuardCompare.CompareOutputs("OSPF neighbors", CheckKind.OspfNeighbors, before, header);
        var lost = Assert.Single(f);
        Assert.Equal(GuardSeverity.Critical, lost.Severity);
        Assert.Contains("10.0.0.2 on GigabitEthernet0/0 is gone", lost.Message);
    }

    [Fact]
    public void RisingErrorCountersWarnButClearedCountersDoNot()
    {
        static string Interfaces(int crc, int input) =>
            "GigabitEthernet0/1 is up, line protocol is up\n" +
            "  Hardware is iGbE, address is 0050.56a1.0001\n" +
            $"     {input} input errors, {crc} CRC, 0 frame, 0 overrun, 0 ignored\n" +
            "     0 output errors, 0 collisions, 1 interface resets\n";

        var rising = GuardCompare.CompareOutputs("Interface errors", CheckKind.InterfaceErrors, Interfaces(0, 0), Interfaces(120, 125));
        var w = Assert.Single(rising);
        Assert.Equal(GuardSeverity.Warning, w.Severity);
        Assert.Contains("GigabitEthernet0/1: CRC +120, input errors +125", w.Details!);

        var cleared = GuardCompare.CompareOutputs("Interface errors", CheckKind.InterfaceErrors, Interfaces(500, 500), Interfaces(0, 0));
        Assert.Equal(GuardSeverity.Info, Assert.Single(cleared).Severity);
    }

    [Fact]
    public void RawChecksShowAddedAndRemovedLines()
    {
        var f = Assert.Single(GuardCompare.CompareOutputs("Failover", CheckKind.Raw, "This host: Primary - Active\n", "This host: Primary - Standby Ready\n"));
        Assert.Contains("- This host: Primary - Active", f.Details!);
        Assert.Contains("+ This host: Primary - Standby Ready", f.Details!);
    }

    [Fact]
    public void ComparesStoredSnapshotsAndFlagsUnreachableDevices()
    {
        var root = Path.Combine(Path.GetTempPath(), "oml-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            GuardSnapshot Snap(string phase, string brief)
            {
                var dir = Path.Combine(root, phase);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "01_brief.txt"), brief);
                File.WriteAllText(Path.Combine(dir, "02_bgp.txt"), "% Invalid input detected at '^' marker.\n");
                return new GuardSnapshot
                {
                    Directory = dir,
                    Results =
                    [
                        new() { Title = "Interfaces", Command = "show ip interface brief", Kind = CheckKind.Interfaces, File = "01_brief.txt" },
                        new() { Title = "BGP peers", Command = "show ip bgp summary", Kind = CheckKind.BgpPeers, File = "02_bgp.txt", Unsupported = true },
                    ],
                };
            }

            var findings = GuardCompare.Compare(Snap("pre", BriefBefore), Snap("post", BriefAfter));
            Assert.Equal(GuardSeverity.Critical, findings[0].Severity); // sorted most severe first
            Assert.DoesNotContain(findings, f => f.Check == "BGP peers");

            var unreachable = GuardCompare.Compare(Snap("pre2", BriefBefore), new GuardSnapshot { Error = "Connection timed out" });
            Assert.Equal("Couldn't connect for the post-check: Connection timed out", Assert.Single(unreachable).Message);

            var report = GuardCompare.Report("CHG001", [("sw1", findings)]);
            Assert.Contains("=== sw1 ===", report);
            Assert.Contains("[CRITICAL] Interfaces:", report);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void CustomProfileTurnsEachLineIntoARawCheck()
    {
        var p = GuardProfiles.Custom(Backup.BackupMode.Exec, "show version\r\rshow clock");
        Assert.Equal(2, p.Checks.Count);
        Assert.All(p.Checks, c => Assert.Equal(CheckKind.Raw, c.Kind));
    }

    [Fact]
    public void EveryBuiltInCheckIsReadOnly()
    {
        foreach (var p in GuardProfiles.All)
            foreach (var c in p.Checks)
                Assert.True(c.Command.StartsWith("show ") || c.Command.StartsWith("get ") || c.Command.StartsWith("ip "), $"{p.Name}: {c.Command}");
    }
}

public class DeviceSessionTests
{
    [Theory]
    [InlineData("SW1#", "SW1")]
    [InlineData("SW1>", "SW1")]
    [InlineData("admin@srx1>", "srx1")]
    [InlineData("asa/ctx1>", "asa")]
    [InlineData("FGT-01 #", "FGT-01")]
    [InlineData("core.sw-1(config)#", "core.sw-1")]
    [InlineData("user@host:~$", null)]
    [InlineData("", null)]
    public void HostnameFromPrompt(string prompt, string? expected) => Assert.Equal(expected, DeviceSession.HostnameFromPrompt(prompt));

    [Theory]
    [InlineData("% Invalid input detected at '^' marker.", true)]
    [InlineData("                   ^\nsyntax error, expecting <command>.", true)]
    [InlineData("Command fail. Return code -61", true)]
    [InlineData("Interface  IP-Address  OK? Method Status  Protocol", false)]
    public void RecognizesCommandErrors(string output, bool error) => Assert.Equal(error, DeviceSession.IsCommandError(output));
}
