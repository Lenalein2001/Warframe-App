"""Collect the tested Windows downloads from one source revision."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
from zipfile import ZipFile


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def check_build_paths(data, name):
    # MSVC and C++ source_location can embed __FILE__ even with symbols excluded.
    for pattern in (rb'[a-z]:[/\\]users[/\\]', rb'[a-z]\0:\0[/\\]\0u\0s\0e\0r\0s\0[/\\]\0'):
        if re.search(pattern, data, re.IGNORECASE):
            raise ValueError(f'Windows user profile path embedded in {name}')


def stage(package, single, ocr, destination):
    repo = Path(__file__).resolve().parents[1]
    commit = subprocess.check_output(['git', '-C', repo, 'rev-parse', 'HEAD'], text=True).strip()
    if destination.exists():
        raise ValueError('Choose a new release directory.')
    portable = package / 'WarframeCompanion-win-x64-portable.zip'
    installer = package / 'WarframeCompanion-win-x64-Setup.exe'
    exe = single / 'portable' / 'WarframeCompanion-win-x64.exe'
    with ZipFile(portable) as archive:
        manifest = json.loads(archive.read('build-manifest.json'))
    single_manifest = json.loads((single / 'package.json').read_text(encoding='utf-8-sig'))
    if manifest['files']['Start Warframe Companion.exe']['sha256'] != single_manifest['sha256'].lower():
        raise ValueError('The ZIP must contain the tested single-file executable.')
    for source in (manifest, single_manifest):
        if source['commit'] != commit or source['uncommittedChanges'] or source['runtime'] != 'win-x64':
            raise ValueError('Package was built from different or uncommitted sources.')
    descriptor = json.loads((repo / 'src/Services/ocr-package.json').read_bytes())
    if ocr.name != descriptor['url'].rsplit('/', 1)[-1] or ocr.stat().st_size != descriptor['bytes']:
        raise ValueError('Incorrect optional OCR asset.')
    expected = {}
    for line in (package / 'SHA256SUMS-windows.txt').read_text(encoding='utf-8-sig').splitlines():
        checksum, name = line.split('  ', 1)
        expected[name] = checksum
    expected[exe.name] = single_manifest['sha256'].lower()
    expected[ocr.name] = descriptor['sha256']
    for path in (portable, ocr):
        with ZipFile(path) as archive:
            for name in archive.namelist():
                if name.lower().endswith(('.dll', '.exe')):
                    check_build_paths(archive.read(name), name)
    check_build_paths(exe.read_bytes(), exe.name)
    destination.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=destination.parent) as temporary:
        output = Path(temporary) / 'windows-x64'
        output.mkdir()
        lines = []
        for asset in sorted((portable, installer, exe, ocr), key=lambda p: p.name):
            target = output / asset.name
            shutil.copyfile(asset, target)
            checksum = digest(target)
            if checksum != expected[asset.name]:
                raise ValueError(f'Checksum mismatch for {asset.name}')
            lines.append(f'{checksum}  {asset.name}')
        (output / 'SHA256SUMS-windows.txt').write_text('\n'.join(lines) + '\n', encoding='ascii', newline='\n')
        output.rename(destination)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('package', 'single', 'ocr', 'destination'):
        parser.add_argument(name, type=Path)
    args = parser.parse_args()
    stage(args.package, args.single, args.ocr, args.destination)
