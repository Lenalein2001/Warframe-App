#!/usr/bin/env bash
set -euo pipefail
version=${1:?Supply a release version}
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || exit 2
pwsh -NoProfile -File tools/publish-companion.ps1 -Runtime linux-x64
package=$(find artifacts -maxdepth 1 -type d -name 'companion-linux-x64-*' | sort | tail -1)
mkdir -p artifacts/linux-release
cp "$package/self-contained.tar.gz" artifacts/linux-release/WarframeCompanion-linux-x64-portable.tar.gz
stage=$(mktemp -d)
trap 'rm -rf -- "$stage"' EXIT
mkdir -p "$stage/opt/warframe-companion" "$stage/usr/bin" "$stage/usr/share/applications" "$stage/DEBIAN"
cp -a "$package/self-contained/." "$stage/opt/warframe-companion/"
rm "$stage/opt/warframe-companion/Portable.sh"
chmod 755 "$stage/opt/warframe-companion/WarframeCompanion.Desktop"
printf '#!/bin/sh\nexec /opt/warframe-companion/WarframeCompanion.Desktop "$@"\n' > "$stage/usr/bin/warframe-companion"
chmod 755 "$stage/usr/bin/warframe-companion"
sed 's/^Exec=.*/Exec=warframe-companion/' packaging/linux/warframe-companion.desktop > "$stage/usr/share/applications/warframe-companion.desktop"
cat > "$stage/DEBIAN/control" <<EOF
Package: warframe-companion
Version: $version
Architecture: amd64
Maintainer: Lenalein2001 <noreply@github.com>
Section: games
Priority: optional
Depends: libgtk-3-0t64, libwebkit2gtk-4.1-0, libgtk-layer-shell0, libnotify4, libfontconfig1, libicu74, libssl3t64, libstdc++6, tesseract-ocr, tesseract-ocr-eng, scrot
Description: Warframe inventory, mastery, crafting and reward companion
 Native desktop host with C# services and C/C++ processing.
EOF
dpkg-deb --root-owner-group --build "$stage" artifacts/linux-release/WarframeCompanion-linux-x64.deb
(cd artifacts/linux-release && sha256sum *.tar.gz *.deb > SHA256SUMS-linux.txt)
