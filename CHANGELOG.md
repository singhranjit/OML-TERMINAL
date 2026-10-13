# Changelog

All notable changes to OML Terminal. Downloads: [omllabs.com/downloads](https://omllabs.com/downloads.html).

## 1.1.0 - 2026-10-13

### Added
- **Voice Assistant** (Windows and Linux) - replaces Voice Command. Talk naturally, in any language and accent, and the task
  gets done on the device in front of you.
  - **Understands what you mean**, not just literal commands: "why is port 2 down?", "bring up port 3 and put it in
    VLAN 20", "open the core switch", "trace to 8.8.8.8", "undo that". It reads the screen to know the device type,
    prompt and configuration mode, and remembers the conversation for follow-ups.
  - **Hears any language:** Whisper speech recognition (99 languages, auto-detected - or pick yours for strong accents).
    Misheard words are corrected from what's on screen.
  - **Does the work in your tab:** commands are typed into the terminal you're watching, `--More--` pages are paged
    through, questions are answered from the real output, and a command the device rejects is corrected once.
  - **Anticipates:** enters and leaves configuration mode itself, and when an answer shows a problem it offers the fix
    ("Shall I bring it up?") - say yes and it's done.
  - **Safe by design:** read-only commands run at once; changes wait for a spoken yes (in any language) or a click;
    reload, erase and similar always need a click. Every request and command is written to an audit log
    (`voice-logs`).
  - **Offline:** speech and the built-in language model (Qwen3 4B, downloaded once, ~2.5 GB) run on your computer.
    Or point it at your own OpenAI-compatible server (LM Studio, Ollama, vLLM, or a cloud API).
  - Replies can be spoken aloud; hands-free mode keeps listening. Session menu > Voice Assistant (Ctrl+Shift+V on
    Windows, Ctrl+Shift+A on Linux); Ctrl+Shift+Space to talk.

### Changed
- Installers carry only their own platform's native libraries (smaller downloads).

## 1.0.0 - 2026-10-12

### Added
- **Update check (opt-in):** a "new version available" banner, from one small request to omllabs.com at most once a day.
  It sends nothing about you or your devices. You can also run Help → Check for Updates, and skip a version you don't want.
- **Welcome tour** on first launch, with the update opt-in and one-click import from PuTTY or MobaXterm when there are no sessions yet.
  Reopen it any time from Help → Welcome Tour.
- **Help menu:** User Guide, What's New (this file) and Check for Updates.
- **New Linux app:** a native desktop app built on the same core as the Windows app, replacing the old Linux build.
  macOS is next.
  - **Terminal:** saved sessions with folders and search; SSH, Telnet, serial and local shell tabs (bash, zsh, fish, pwsh in
    a real pseudo-terminal); find in scrollback; zoom; session logging; split view with synchronized typing; macros; a
    multi-execution bar; troubleshooting workflows; keyword highlighting.
  - **SSH extras:** an SFTP browser (browse, upload, download, edit, rename, delete) and SSH tunnels (local, remote, SOCKS).
  - **Every tool from the Windows app**, including Visual Trace and WireWalk, Ping Monitor, MRTG graphs, Topology Mapper,
    Wi-Fi Analyzer (using `iw`/NetworkManager), Packet Capture and Analyzer (libpcap/tshark), config backups, Change Guard,
    the firewall builders and migration, Password Manager, the network services and the AI Copilot.
  - **Import and export:** sessions from PuTTY, MobaXterm, SecureCRT, CSV/JSON; export sessions, a shareable config and workflows.
  - **Packages:** a `.deb` (installs to `/opt/oml-terminal` and grants the raw-socket and capture capabilities, so traceroute,
    ping monitoring and live capture work without root) and an AppImage (falls back to the system `ping` for traceroute).

### Security
- **Session passwords are never stored in plain text.** Without a master password, session passwords, enable passwords,
  key passphrases and jump-host passwords were written to `sessions.json` as plain text. They're now encrypted for your
  user account on this computer (Windows DPAPI; on Linux/macOS a key file readable only by you). Existing files are
  encrypted on the next save. Note: an older version can't read the encrypted passwords if you downgrade.
  An export you choose to include passwords in is still written in plain text, so it can be imported on another computer.

### Fixed
- **FTP server:** a passive port range entered high-to-low (e.g. 50100-50000) offered no ports at all; it now works either way.

### Changed
- **About** shows the version, every tool, the project links and where your data is stored.

## 0.3.8 - 2026-10-09

### Added
- **WireWalk:** the hop-by-hop device path in Visual Trace now has its own name and tab.

### Fixed
- **Damaged data files are kept.** If sessions, settings, the password vault, known hosts, the ping list or MRTG data can't be read,
  the file is saved as `<name>.unreadable-<time>` before the app starts fresh. Previously the next save overwrote it.
  Session profiles dropped as invalid on load are kept the same way.
- **Tool tabs keep their state** when you switch away and back. Previously the start device, capture adapter, a replayed Wi-Fi scan
  and a half-edited password entry were reset.
- **An unexpected error in a tool no longer closes the app**, which used to take every open session with it.
  The error is logged to `crash.log` and shown in a banner.
- **Ping Monitor scales:** ICMP probes are asynchronous. With 300 unreachable hosts the app now uses 3 pool threads instead of about 300.
- **Packet Analyzer:**
  - packets use 57% less memory (the detail tree is built when you open a packet)
  - very large capture files are read up to 512 MB instead of being loaded whole
  - connections rejected with ICMP "unreachable" are reported as rejected by that device, not as "no answer"
- **Display filters:** a bare text field (`tls.sni`, `http.host`, `dns.qry.name`) now means "present".
- **MRTG:** two polls of one slow device no longer overlap, and a removed device stays removed.
- **Visual Trace** picks up new config backups while its tab is open.
- **Process handles** are now released after launching Wireshark or Explorer.

## 0.3.7 - 2026-10-08

### Added
- **Visual Trace:** a continuous traceroute (MTR-style) drawn as a path.
  - Per-hop loss, latency and jitter, with hop latency from Windows' own ICMP timing.
  - A plain-English verdict that tells real loss from ICMP rate limiting, and spots latency jumps, loops and route changes.
  - Call quality as a MOS score.
  - Hop owners from your backups and sessions, plus AS numbers and names for public hops.
- **Device path (WireWalk):**
  - hop by hop over SSH through routing tables and CDP/LLDP, following every equal-cost path
  - interface load, errors and drops at every hop
  - ARP and MAC tables down to the host's switch port
- **Multicast trace:** an RPF walk (RPF, (S,G)/(*,G) state, counters, PIM, IGMP) and the router's own `mtrace`.
- **Ping Monitor:** many hosts at once with latency, loss, jitter and MOS tiles, an outage log and CSV export.
- **Traffic Graphs (MRTG):**
  - SNMP v1/v2c/v3, with MD5/SHA-1/SHA-2 authentication and DES/AES-128/192/256 privacy
  - live, daily, weekly, monthly and yearly graphs
  - 95th percentile, alerts and background polling
- **Packet Analyzer:** capture with Npcap (or a raw socket), open pcap and pcapng files, Wireshark-style display filters,
  and a Problems panel (duplicate IPs, refused or unanswered connections, retransmissions, DNS/DHCP failures, ICMP errors, STP changes).
- **Wi-Fi Analyzer:** nearby networks, channel planning for 2.4, 5 and 6 GHz, a roaming log, and floor-plan site surveys with heatmaps.
- A documented README with screenshots, and a sample capture in `docs/samples`.

### Fixed
- The Wi-Fi Analyzer crashed when opened.
- Device commands now return as soon as the prompt reappears, which makes Topology, Backup and Change Guard much faster.

## 0.3.6 - 2026-10-08

### Added
- **SSH host key checking:** trust on first use, with a clear warning if a device's key changes, and a "Forget saved host key" command.

### Fixed
- Global Search can read files that are still open for writing, and ranks results by relevance.
- Topology Mapper marks devices as stopped when a crawl is cancelled, and no longer redraws while you drag a node.
- Config Backup rejects "invalid command" output instead of saving it over your history.
- ASA parser: network groups after the source, and object-group services in the protocol position.
- Route table parser handles `O*E2` routes.
- Host Monitor columns widened.

## 0.3.5 - 2026-10

### Added
- **Topology Mapper:** discover the network from one device over CDP/LLDP and draw a live, clickable map.
- **Change Guard:** pre/post change snapshots compared in plain English.
- **Global Search:** find an IP, subnet, MAC or text across every backup, log, capture and session (Ctrl+Shift+G).
- **Structured Output:** turn `show` command output into a sortable, filterable table.

## 0.3.4 - 2026-09-29

### Fixed
- Config Migration output was hidden behind long review lists.

## 0.3.3 and earlier - September 2026

The first public releases:
- **Connections:** SSH (built-in, OpenSSH or PuTTY), Telnet, Serial, RDP, VNC, SFTP and local shells.
- **Working in the terminal:** split view, MultiExec, tunnels, X11 forwarding, macros and voice commands.
- **Sessions:** a password manager, and importers from PuTTY, MobaXterm and SecureCRT.
- **Toolkit:** Port Query, Local Ports, Ping/Trace/Sweep, DNS, Subnet Calculator, Packet Capture on devices, Host Monitor,
  Firewall Object and Policy Builders, Config Migration, Config Backup and Scheduled Backups, CLI Guide, Network Services
  and the AI Copilot.
