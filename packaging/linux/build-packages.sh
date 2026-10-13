#!/usr/bin/env bash
# Builds the Linux packages of OML Terminal (the Avalonia app in src/OmlTerminal.Desktop):
#   publish/linux/oml-terminal_<version>_<arch>.deb
#   publish/linux/OML-Terminal-<version>-<arch>.AppImage
# Run on Linux with the .NET 10 SDK and dpkg-deb. ARCH=x64 (default) or arm64.
# The AppImage step downloads appimagetool once into publish/linux/tools (SKIP_APPIMAGE=1 skips it).
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/../.." && pwd)
HERE="$ROOT/packaging/linux"
OUT="${OUT:-$ROOT/publish/linux}"
ARCH="${ARCH:-x64}"
case "$ARCH" in
  x64)   DEBARCH=amd64; APPARCH=x86_64 ;;
  arm64) DEBARCH=arm64; APPARCH=aarch64 ;;
  *) echo "ARCH must be x64 or arm64" >&2; exit 1 ;;
esac
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/src/OmlTerminal.Desktop/OmlTerminal.Desktop.csproj" | head -1)
DOTNET="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
echo "OML Terminal $VERSION for linux-$ARCH -> $OUT"

# ---------- publish ----------
STAGE="$OUT/stage-$ARCH"
rm -rf "$STAGE"
"$DOTNET" publish "$ROOT/src/OmlTerminal.Desktop/OmlTerminal.Desktop.csproj" -c Release -r "linux-$ARCH" --self-contained true \
  -p:DebugType=none -p:DebugSymbols=false -o "$STAGE" --nologo -v quiet
chmod 755 "$STAGE/oml-terminal"
# The speech and language-model engines ship native libraries for every platform; keep only this one.
if [ -d "$STAGE/runtimes" ]; then
  find "$STAGE/runtimes" -mindepth 1 -maxdepth 1 -type d ! -name "linux-$ARCH" -exec rm -rf {} +
fi
rm -f "$STAGE/ggml-metal.metal"

install_icons() { # $1 = share dir
  for s in 48 64 128 256 512; do
    install -Dm644 "$HERE/icons/oml-terminal-$s.png" "$1/icons/hicolor/${s}x${s}/apps/oml-terminal.png"
  done
}

# ---------- .deb ----------
DEB="$OUT/deb-$ARCH"
rm -rf "$DEB"
mkdir -p "$DEB/DEBIAN" "$DEB/opt/oml-terminal" "$DEB/usr/bin"
cp -a "$STAGE/." "$DEB/opt/oml-terminal/"
ln -s /opt/oml-terminal/oml-terminal "$DEB/usr/bin/oml-terminal"
install -Dm644 "$HERE/oml-terminal.desktop" "$DEB/usr/share/applications/oml-terminal.desktop"
install_icons "$DEB/usr/share"
SIZE=$(du -sk "$DEB" | cut -f1)
cat > "$DEB/DEBIAN/control" <<EOF
Package: oml-terminal
Version: $VERSION
Architecture: $DEBARCH
Maintainer: OML Labs <https://omllabs.com>
Homepage: https://omllabs.com/oml-terminal.html
Section: net
Priority: optional
Installed-Size: $SIZE
Depends: libc6, libgcc-s1, libstdc++6, zlib1g, libfontconfig1, libx11-6, libice6, libsm6, libicu74 | libicu72 | libicu71 | libicu70 | libicu67 | libicu66 | libicu-dev
Recommends: libcap2-bin, libpcap0.8, iw, xdg-utils, fonts-noto-core, fonts-noto-cjk, pulseaudio-utils | pipewire-bin | alsa-utils, espeak-ng
Suggests: tshark, network-manager
Description: SSH, Telnet, serial and local shells with a network engineer's toolkit
 OML Terminal is a terminal for network engineers: SSH, Telnet, serial and
 local shells with saved sessions, SFTP and tunnels, plus Visual Trace,
 ping monitoring, MRTG graphs over SNMP, packet capture and analysis, Wi-Fi
 analysis, topology discovery, config backups and change checks.
 .
 The installer grants the program raw-socket and capture capabilities
 (cap_net_raw, cap_net_admin, cap_net_bind_service) so traceroute, ping
 monitoring, live capture and the built-in TFTP/syslog/DHCP services work
 without running as root.
EOF
cat > "$DEB/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e
if [ "$1" = "configure" ] && command -v setcap >/dev/null 2>&1; then
  setcap 'cap_net_raw,cap_net_admin,cap_net_bind_service+ep' /opt/oml-terminal/oml-terminal || \
    echo "oml-terminal: couldn't set network capabilities - ICMP tools fall back to the system ping." >&2
fi
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t /usr/share/icons/hicolor || true
exit 0
EOF
chmod 755 "$DEB/DEBIAN/postinst"
DEBFILE="$OUT/oml-terminal_${VERSION}_${DEBARCH}.deb"
dpkg-deb --root-owner-group -Zxz --build "$DEB" "$DEBFILE" >/dev/null
echo "built $DEBFILE"

# ---------- AppImage ----------
if [ "${SKIP_APPIMAGE:-0}" != "1" ]; then
  APPDIR="$OUT/AppDir-$ARCH"
  rm -rf "$APPDIR"
  mkdir -p "$APPDIR/usr/lib/oml-terminal" "$APPDIR/usr/bin"
  cp -a "$STAGE/." "$APPDIR/usr/lib/oml-terminal/"
  ln -s ../lib/oml-terminal/oml-terminal "$APPDIR/usr/bin/oml-terminal"
  install -Dm644 "$HERE/oml-terminal.desktop" "$APPDIR/oml-terminal.desktop"
  install -Dm644 "$HERE/oml-terminal.desktop" "$APPDIR/usr/share/applications/oml-terminal.desktop"
  install_icons "$APPDIR/usr/share"
  cp "$HERE/icons/oml-terminal-256.png" "$APPDIR/oml-terminal.png"
  ln -s oml-terminal.png "$APPDIR/.DirIcon"
  cat > "$APPDIR/AppRun" <<'EOF'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/lib/oml-terminal/oml-terminal" "$@"
EOF
  chmod 755 "$APPDIR/AppRun"
  TOOL="$OUT/tools/appimagetool-$APPARCH.AppImage"
  if [ ! -x "$TOOL" ]; then
    mkdir -p "$OUT/tools"
    curl -fsSL -o "$TOOL" "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-$APPARCH.AppImage"
    chmod 755 "$TOOL"
  fi
  APPFILE="$OUT/OML-Terminal-$VERSION-$APPARCH.AppImage"
  # Extract-and-run: no FUSE needed on the build machine.
  APPIMAGE_EXTRACT_AND_RUN=1 ARCH=$APPARCH "$TOOL" --no-appstream "$APPDIR" "$APPFILE" >/dev/null 2>&1
  echo "built $APPFILE"
fi

ls -la "$OUT"/*.deb "$OUT"/*.AppImage 2>/dev/null || true
