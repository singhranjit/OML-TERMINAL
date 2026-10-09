using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace OmlTerminal.Core.Wifi;

/// <summary>Picks the platform's Wi-Fi source: Windows Native Wi-Fi, or iw/NetworkManager on Linux.</summary>
public static class WifiSources
{
    public static IWifiSource CreateLocal()
    {
        if (OperatingSystem.IsWindows()) return new WlanSource();
        if (OperatingSystem.IsLinux()) return new LinuxWifiSource();
        throw new WifiUnavailableException("Live Wi-Fi scanning isn't available on this system yet - open a recorded scan instead.");
    }
}

/// <summary>
/// Linux Wi-Fi without root: <c>iw dev X scan dump</c> reads the kernel's cached scan results (signal in dBm, channel
/// width, security, station count), and NetworkManager (<c>nmcli</c>) triggers rescans, which an ordinary desktop user is
/// allowed to do. Where iw isn't installed, nmcli's own list is used (signal as a percentage, so dBm is estimated).
/// </summary>
public sealed class LinuxWifiSource : IWifiSource
{
    private readonly Dictionary<Guid, string> _names = new();
    private readonly bool _hasIw = Which("iw"), _hasNmcli = Which("nmcli");

    public LinuxWifiSource()
    {
        if (!Directory.Exists("/sys/class/net")) throw new WifiUnavailableException("No network interfaces are visible.");
        if (!_hasIw && !_hasNmcli)
            throw new WifiUnavailableException("Wi-Fi scanning needs the 'iw' tool or NetworkManager (nmcli). Install iw (e.g. sudo apt install iw) and try again.");
    }

    public IReadOnlyList<WifiAdapter> Adapters()
    {
        var list = new List<WifiAdapter>();
        foreach (var dir in Directory.EnumerateDirectories("/sys/class/net").Order())
        {
            if (!Directory.Exists(Path.Combine(dir, "wireless")) && !File.Exists(Path.Combine(dir, "phy80211", "name"))) continue;
            var name = Path.GetFileName(dir);
            var id = IdFor(name);
            _names[id] = name;
            string state = "Disconnected";
            try { if (File.ReadAllText(Path.Combine(dir, "operstate")).Trim() == "up") state = "Connected"; } catch { }
            list.Add(new WifiAdapter(id, name, state));
        }
        if (list.Count == 0) throw new WifiUnavailableException("No Wi-Fi adapter found on this computer.");
        return list;
    }

    /// <summary>Interfaces have names, not GUIDs: derive a stable one from the name.</summary>
    public static Guid IdFor(string interfaceName) => new(MD5.HashData(Encoding.UTF8.GetBytes("wifi:" + interfaceName)));

    private string NameOf(Guid id) => _names.TryGetValue(id, out var n) ? n : throw new WifiUnavailableException("That Wi-Fi adapter is gone.");

    public void RequestScan(Guid adapter)
    {
        var name = NameOf(adapter);
        // Fire and forget: results land in the cache that GetNetworks reads a few seconds later.
        if (_hasNmcli) _ = Run("nmcli", ["device", "wifi", "rescan", "ifname", name], 15000);
        else _ = Run("iw", ["dev", name, "scan", "trigger"], 15000); // needs CAP_NET_ADMIN; harmless if refused
    }

    public IReadOnlyList<WifiNetwork> GetNetworks(Guid adapter)
    {
        var name = NameOf(adapter);
        if (_hasIw)
        {
            var dump = Run("iw", ["dev", name, "scan", "dump"], 10000).GetAwaiter().GetResult();
            if (dump.Exit == 0) return ParseIwScan(dump.Output);
            if (!_hasNmcli) throw new WifiUnavailableException("iw couldn't read scan results: " + dump.Error.Trim());
        }
        var nm = Run("nmcli", ["-t", "-e", "yes", "-f", "BSSID,SSID,CHAN,FREQ,RATE,SIGNAL,SECURITY,BANDWIDTH", "device", "wifi", "list", "ifname", name, "--rescan", "no"], 10000)
            .GetAwaiter().GetResult();
        if (nm.Exit != 0) throw new WifiUnavailableException("NetworkManager couldn't list Wi-Fi networks: " + nm.Error.Trim());
        return ParseNmcli(nm.Output);
    }

    public WifiConnection? CurrentConnection(Guid adapter)
    {
        if (!_hasIw) return null;
        var link = Run("iw", ["dev", NameOf(adapter), "link"], 5000).GetAwaiter().GetResult();
        return link.Exit == 0 ? ParseIwLink(link.Output) : null;
    }

    public void Dispose() { }

    // ---------- parsers (public for tests) ----------

    private static readonly Regex BssLine = new(@"^BSS ([0-9a-f:]{17})", RegexOptions.IgnoreCase);

    /// <summary>Parses <c>iw dev X scan dump</c>. Indented "key: value" lines follow each "BSS aa:bb:..." header.</summary>
    public static IReadOnlyList<WifiNetwork> ParseIwScan(string text)
    {
        var result = new List<WifiNetwork>();
        WifiNetwork? n = null;
        string section = "";
        bool privacy = false, rsn = false, wpa = false, ht = false, vht = false, he = false, eht = false;
        int htOffset = 0, width = 20;
        bool htAnyWidth = false;
        var akm = new StringBuilder();

        void Finish()
        {
            if (n is null) return;
            if (htAnyWidth && htOffset != 0) width = Math.Max(width, 40);
            n.ChannelWidth = width;
            n.Security = Security(rsn, wpa, privacy, akm.ToString());
            n.Standard = eht ? "Wi-Fi 7 (be)"
                : he ? (n.Band == WifiBand.Band6 ? "Wi-Fi 6E (ax)" : "Wi-Fi 6 (ax)")
                : vht ? "Wi-Fi 5 (ac)"
                : ht ? "Wi-Fi 4 (n)"
                : n.Band == WifiBand.Band2_4 && n.MaxBasicRateMbps <= 11 ? "802.11b"
                : n.Band == WifiBand.Band2_4 ? "802.11g" : "802.11a";
            n.CoveredChannels = WifiMath.Covered(n.Channel, n.ChannelWidth, n.Band, htOffset);
            n.LinkQuality = Math.Clamp(2 * (n.Rssi + 100), 0, 100);
            result.Add(n);
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (BssLine.Match(line) is { Success: true } bss)
            {
                Finish();
                n = new WifiNetwork { Bssid = bss.Groups[1].Value.ToLowerInvariant() };
                section = ""; privacy = rsn = wpa = ht = vht = he = eht = htAnyWidth = false; htOffset = 0; width = 20; akm.Clear();
                continue;
            }
            if (n is null) continue;
            int depth = line.TakeWhile(c => c == '\t').Count();
            var t = line.Trim();
            if (t.Length == 0) continue;
            if (depth <= 1)
            {
                int colon = t.IndexOf(':');
                section = colon > 0 ? t[..colon] : t;
                var value = colon > 0 ? t[(colon + 1)..].Trim() : "";
                switch (section)
                {
                    case "freq":
                        n.FrequencyMHz = (int)double.Parse(value, CultureInfo.InvariantCulture);
                        (n.Channel, n.Band) = WifiMath.ChannelFor(n.FrequencyMHz);
                        break;
                    case "beacon interval": n.BeaconIntervalTu = LeadingInt(value); break;
                    case "capability": privacy = value.Contains("Privacy"); break;
                    case "signal": n.Rssi = (int)Math.Round(double.Parse(value.Split(' ')[0], CultureInfo.InvariantCulture)); break;
                    case "SSID": n.Ssid = UnescapeIw(value); break;
                    case "Supported rates":
                    case "Extended supported rates":
                        foreach (var r in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                            if (double.TryParse(r.TrimEnd('*'), NumberStyles.Float, CultureInfo.InvariantCulture, out var mbps))
                                n.MaxBasicRateMbps = Math.Max(n.MaxBasicRateMbps, mbps);
                        break;
                    case "DS Parameter set": if (n.Channel == 0) n.Channel = LeadingInt(value.Replace("channel", "")); break;
                    case "Country": n.Country = value.Length >= 2 ? value[..2] : null; break;
                    case "RSN": rsn = true; AppendAkm(value); break;
                    case "WPA": wpa = true; break;
                    case "HT capabilities": case "HT operation": ht = true; break;
                    case "VHT capabilities": case "VHT operation": vht = true; break;
                    case "HE capabilities": case "HE operation": he = true; break;
                    case "EHT capabilities": case "EHT operation": eht = true; break;
                }
                continue;
            }
            // Sub-items: "* name: value" under the current section.
            var item = t.TrimStart('*').Trim();
            int c2 = item.IndexOf(':');
            if (c2 < 0) continue;
            string key = item[..c2].Trim(), val = item[(c2 + 1)..].Trim();
            switch (section, key)
            {
                case ("BSS Load", "station count"): n.StationCount = LeadingInt(val); break;
                case ("BSS Load", "channel utilisation"):
                    var parts = val.Split('/');
                    if (parts.Length == 2 && int.TryParse(parts[0], out var used) && int.TryParse(parts[1], out var of) && of > 0)
                        n.ChannelUtilization = (int)Math.Round(used * 100.0 / of);
                    break;
                case ("HT operation", "secondary channel offset"):
                    htOffset = val.StartsWith("above") ? 1 : val.StartsWith("below") ? -1 : 0;
                    break;
                case ("HT operation", "STA channel width"): htAnyWidth = val.StartsWith("any"); break;
                case ("VHT operation", "channel width"):
                    int w = LeadingInt(val);
                    if (w == 1) width = Math.Max(width, val.Contains("160") ? 160 : 80);
                    else if (w is 2 or 3) width = Math.Max(width, 160);
                    break;
                case ("VHT operation", "center freq segment 2"):
                    if (LeadingInt(val) != 0 && width == 80) width = 160; // 80 MHz with a second segment = 160 / 80+80
                    break;
                case ("HE operation", "6 GHz Operation Information") or ("HE operation", "Channel Width"):
                    width = Math.Max(width, val.Contains("320") ? 320 : val.Contains("160") ? 160 : val.Contains("80") ? 80 : val.Contains("40") ? 40 : 20);
                    break;
                case ("RSN", "Authentication suites"): AppendAkm(val); break;
            }
        }
        Finish();
        return result;

        void AppendAkm(string v) => akm.Append(' ').Append(v);
    }

    private static string Security(bool rsn, bool wpa, bool privacy, string akm)
    {
        if (!rsn) return wpa ? "WPA" : privacy ? "WEP" : "Open";
        bool psk = Regex.IsMatch(akm, @"\bPSK\b|PSK/SHA-256"), sae = akm.Contains("SAE");
        bool ent = akm.Contains("IEEE 802.1X") || akm.Contains("802.1X");
        string name =
            akm.Contains("OWE") ? "OWE (Enhanced Open)"
            : akm.Contains("SHA384") || akm.Contains("Suite-B") ? "WPA3-Enterprise 192-bit"
            : sae && psk ? "WPA2/WPA3-Personal"
            : sae ? "WPA3-Personal"
            : ent ? "WPA2-Enterprise"
            : psk ? "WPA2-Personal"
            : "WPA2";
        return wpa && name.StartsWith("WPA2") ? "WPA/" + name : name;
    }

    private static int LeadingInt(string s)
    {
        var m = Regex.Match(s, @"-?\d+");
        return m.Success ? int.Parse(m.Value, CultureInfo.InvariantCulture) : 0;
    }

    /// <summary>iw prints non-printable SSID bytes as \xNN.</summary>
    private static string UnescapeIw(string s)
    {
        if (!s.Contains("\\x")) return s;
        var bytes = new List<byte>();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 3 < s.Length && s[i + 1] == 'x' && byte.TryParse(s.AsSpan(i + 2, 2), NumberStyles.HexNumber, null, out var b))
            { bytes.Add(b); i += 3; }
            else bytes.AddRange(Encoding.UTF8.GetBytes(s[i].ToString()));
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>Parses <c>nmcli -t -e yes -f BSSID,SSID,CHAN,FREQ,RATE,SIGNAL,SECURITY,BANDWIDTH device wifi list</c>.
    /// Terse mode separates fields with ':' and escapes literal ones as '\:'.</summary>
    public static IReadOnlyList<WifiNetwork> ParseNmcli(string text)
    {
        var list = new List<WifiNetwork>();
        foreach (var raw in text.Split('\n'))
        {
            var f = SplitTerse(raw.TrimEnd('\r'));
            if (f.Count < 7) continue;
            int mhz = LeadingInt(f[3]), signal = LeadingInt(f[5]);
            var (channel, band) = WifiMath.ChannelFor(mhz);
            if (channel == 0) channel = LeadingInt(f[2]);
            int width = f.Count > 7 ? Math.Max(20, LeadingInt(f[7])) : 20;
            var sec = f[6].Trim();
            var n = new WifiNetwork
            {
                Bssid = f[0].ToLowerInvariant(),
                Ssid = f[1],
                Channel = channel,
                Band = band,
                FrequencyMHz = mhz,
                LinkQuality = signal,
                Rssi = signal / 2 - 100, // NetworkManager's percentage is derived from dBm this way
                ChannelWidth = width,
                Security = sec is "" or "--" ? "Open"
                    : sec.Contains("WPA3") && sec.Contains("WPA2") ? "WPA2/WPA3-Personal"
                    : sec.Contains("WPA3") ? "WPA3-Personal"
                    : sec.Contains("802.1X") ? "WPA2-Enterprise"
                    : sec.Contains("WPA2") ? (sec.Contains("WPA1") ? "WPA/WPA2-Personal" : "WPA2-Personal")
                    : sec.Contains("WPA1") ? "WPA" : sec.Contains("WEP") ? "WEP" : sec,
                Standard = width >= 80 ? "Wi-Fi 5 (ac)" : "",
            };
            n.CoveredChannels = WifiMath.Covered(n.Channel, n.ChannelWidth, n.Band);
            list.Add(n);
        }
        return list;
    }

    private static List<string> SplitTerse(string line)
    {
        var fields = new List<string>();
        var cur = new StringBuilder();
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '\\' && i + 1 < line.Length) { cur.Append(line[++i]); continue; }
            if (line[i] == ':') { fields.Add(cur.ToString()); cur.Clear(); continue; }
            cur.Append(line[i]);
        }
        fields.Add(cur.ToString());
        return fields;
    }

    /// <summary>Parses <c>iw dev X link</c>: "Connected to aa:bb:... (on wlan0)", then SSID, signal and bitrates.</summary>
    public static WifiConnection? ParseIwLink(string text)
    {
        var bssid = Regex.Match(text, @"Connected to ([0-9a-f:]{17})", RegexOptions.IgnoreCase);
        if (!bssid.Success) return null;
        string ssid = Regex.Match(text, @"^\s*SSID:\s*(.*)$", RegexOptions.Multiline).Groups[1].Value.Trim();
        int rssi = LeadingInt(Regex.Match(text, @"signal:\s*(-?\d+)").Groups[1].Value);
        double Rate(string key) => double.TryParse(Regex.Match(text, key + @" bitrate:\s*([\d.]+)").Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        return new WifiConnection(UnescapeIw(ssid), bssid.Groups[1].Value.ToLowerInvariant(), rssi, Math.Clamp(2 * (rssi + 100), 0, 100),
            (int)Rate("rx"), (int)Rate("tx"), UnescapeIw(ssid));
    }

    // ---------- process helpers ----------

    private static bool Which(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin").Split(':').Append("/usr/sbin").Append("/sbin")
            .Any(d => File.Exists(Path.Combine(d, tool)));

    private static async Task<(int Exit, string Output, string Error)> Run(string tool, IReadOnlyList<string> args, int timeoutMs)
    {
        var psi = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["LC_ALL"] = "C";
        try
        {
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeoutMs);
            try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { try { p.Kill(); } catch { } return (-1, "", tool + " timed out"); }
            return (p.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (System.ComponentModel.Win32Exception e) { return (-1, "", e.Message); }
    }
}
