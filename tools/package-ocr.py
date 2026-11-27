"""Prepare the optional Windows OCR release asset and its pinned application descriptor."""
import argparse
import io
import hashlib
import json
import re
from pathlib import Path
import zipfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('source', type=Path)
parser.add_argument('output', type=Path)
parser.add_argument('--descriptor', type=Path, required=True)
parser.add_argument('--release-tag', default='v' + (Path(__file__).resolve().parents[1] / 'VERSION').read_text().strip())
args = parser.parse_args()
if not re.fullmatch(r'(?:v[0-9]+\.[0-9]+\.[0-9]+|ocr-[a-zA-Z0-9.-]+)', args.release_tag):
    raise ValueError('Invalid release tag')
names = ['tesseract.exe', 'tessdata/eng.traineddata', 'licenses/BUILD.txt',
         'licenses/Leptonica.txt', 'licenses/Tesseract.txt']
files = {}
buffer = io.BytesIO()
with zipfile.ZipFile(buffer, 'w', compression=zipfile.ZIP_DEFLATED) as archive:
    for name in names:
        path = args.source / name
        if path.is_symlink():
            raise ValueError('Symbolic links are not release files')
        content = path.read_bytes()
        files[name] = {'bytes': len(content), 'sha256': hashlib.sha256(content).hexdigest()}
        info = zipfile.ZipInfo(name, (2026, 1, 1, 0, 0, 0))
        info.compress_type = zipfile.ZIP_DEFLATED
        archive.writestr(info, content)
archive_bytes = buffer.getvalue()
digest = hashlib.sha256(archive_bytes).hexdigest()
url = f'https://github.com/Lenalein2001/Warframe-App/releases/download/{args.release_tag}/WarframeCompanion-OCR-win-x64.zip'
if args.output.name != 'WarframeCompanion-OCR-win-x64.zip':
    raise ValueError('Use the release asset filename WarframeCompanion-OCR-win-x64.zip')
if args.descriptor.exists():
    previous = json.loads(args.descriptor.read_text(encoding='utf-8'))
    if previous['url'] == url and previous['sha256'] != digest:
        raise ValueError('OCR bytes changed. Choose a new --release-tag; never replace a pinned release asset.')
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_bytes(archive_bytes)
descriptor = {'version': digest[:16], 'url': url,
              'bytes': args.output.stat().st_size, 'sha256': digest, 'files': files}
args.descriptor.write_text(json.dumps(descriptor, indent=2) + '\n', encoding='utf-8')
print(f'{args.output}: {descriptor["bytes"]} bytes; SHA256 {digest}')
