#!/usr/bin/env bash
set -euo pipefail
version=$(tr -d '\r\n' < VERSION)
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || exit 2
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT
package="$work/package"
stage="$work/debian"
pwsh -NoProfile -File tools/publish-companion.ps1 -Runtime linux-x64 -OutputDirectory "$package"
mkdir -p artifacts/linux-release
cp "$package/WarframeCompanion-linux-x64-portable.tar.gz" artifacts/linux-release/WarframeCompanion-linux-x64-portable.tar.gz
mkdir -p "$stage/opt/warframe-companion" "$stage/usr/bin" "$stage/usr/share/applications" "$stage/DEBIAN"
cp -a "$package/framework-dependent/." "$stage/opt/warframe-companion/"
chmod 755 "$stage/opt/warframe-companion/WarframeCompanion.Desktop"
printf '#!/bin/sh\nexec /opt/warframe-companion/WarframeCompanion.Desktop "$@"\n' > "$stage/usr/bin/warframe-companion"
chmod 755 "$stage/usr/bin/warframe-companion"
sed 's/^Exec=.*/Exec=warframe-companion/' packaging/linux/warframe-companion.desktop > "$stage/usr/share/applications/warframe-companion.desktop"
install -Dm644 assets/icon.svg "$stage/usr/share/icons/hicolor/scalable/apps/warframe-companion.svg"
cat > "$stage/DEBIAN/control" <<EOF
Package: warframe-companion
Version: $version
Architecture: amd64
Maintainer: Lenalein2001 <noreply@github.com>
Section: games
Priority: optional
Depends: aspnetcore-runtime-10.0, libgtk-3-0t64, libwebkit2gtk-4.1-0, libgtk-layer-shell0, libnotify4, libfontconfig1, libicu74, libssl3t64, libstdc++6
Suggests: tesseract-ocr, tesseract-ocr-eng, scrot
Description: Warframe inventory, mastery, crafting and reward companion
 Native desktop host with C# services and C/C++ processing.
EOF
dpkg-deb --root-owner-group --build "$stage" artifacts/linux-release/WarframeCompanion-linux-x64.deb
(cd artifacts/linux-release && sha256sum WarframeCompanion-linux-x64-portable.tar.gz WarframeCompanion-linux-x64.deb > SHA256SUMS-linux.txt)
