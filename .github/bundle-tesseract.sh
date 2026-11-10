#!/usr/bin/env bash
# Packs tesseract for the Windows exe: the MSYS2 build, every DLL it loads from MSYS2, the English
# model the Linux version reads with, and the licenses of all of it. Runs in the Windows
# workflow's MSYS2 UCRT64 shell, after `pacman -S mingw-w64-ucrt-x86_64-tesseract-ocr zip`.
#
#   bash .github/bundle-tesseract.sh OUT.zip
set -euo pipefail

out=$(realpath -m "$1")
prefix=/ucrt64
work=$(mktemp -d)
dir="$work/tesseract"
mkdir -p "$dir/tessdata" "$dir/licenses"

cp "$prefix/bin/tesseract.exe" "$dir/"
# ldd lists every DLL the exe loads, recursively. Windows' own DLLs are on every machine.
ldd "$prefix/bin/tesseract.exe" | awk -v p="$prefix/" 'index($3, p) == 1 { print $3 }' | sort -u |
  while read -r dll; do cp "$dll" "$dir/"; done

# Keep the original LSTM model and dictionaries, without the unused legacy
# classifier. Use Tesseract's own packer instead of maintaining a format writer.
curl -fsSL -o "$work/original.traineddata" \
  https://raw.githubusercontent.com/tesseract-ocr/tessdata/4.1.0/eng.traineddata
echo "daa0c97d651c19fba3b25e81317cd697e9908c8208090c94c3905381c23fc047  $work/original.traineddata" |
  sha256sum -c -
components=(lstm lstm-punc-dawg lstm-word-dawg lstm-number-dawg lstm-unicharset lstm-recoder version)
extract=()
mkdir -p "$work/model"
for component in "${components[@]}"; do extract+=("$work/model/eng.$component"); done
"$prefix/bin/combine_tessdata.exe" -e "$work/original.traineddata" "${extract[@]}"
"$prefix/bin/combine_tessdata.exe" "$work/model/eng."
cp "$work/model/eng.traineddata" "$dir/tessdata/eng.traineddata"
echo "906538558589e563fad743a10a7c4d4e041112c9d546b3f648ad42ad703e679f  $dir/tessdata/eng.traineddata" |
  sha256sum -c -
cp -r "$prefix/share/tessdata/configs" "$dir/tessdata/"

# Tesseract is Apache 2.0; its libraries carry their own licenses.
for f in "$dir"/*.exe "$dir"/*.dll; do pacman -Qqo "$prefix/bin/$(basename "$f")"; done | sort -u > "$work/packages"
while read -r pkg; do
  name=${pkg#mingw-w64-ucrt-x86_64-}
  if [ -d "$prefix/share/licenses/$name" ]; then cp -r "$prefix/share/licenses/$name" "$dir/licenses/"; fi
  pacman -Qi "$pkg" | grep -E '^(Name|Version|URL|Licenses)' >> "$dir/licenses/PACKAGES.txt"
  echo >> "$dir/licenses/PACKAGES.txt"
done < "$work/packages"

rm -f "$out"
(cd "$dir" && zip -qr9 "$out" .)
echo "$(ls "$dir" | wc -l) files, $(du -sh "$dir" | cut -f1) unpacked, $(du -h "$out" | cut -f1) zipped"
"$dir/tesseract.exe" --version | head -1
