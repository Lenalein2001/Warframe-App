"""A rejected OCR rebuild must leave the previously pinned asset usable."""
from pathlib import Path
import runpy
import subprocess
import sys
import tempfile

tools = Path(__file__).resolve().parents[1] / 'tools'
check = runpy.run_path(str(tools / 'stage-windows-release.py'))['check_build_paths']
for encoding in ('ascii', 'utf-16-le'):
    check('./notifications_windows.cpp'.encode(encoding), 'clean.dll')
    for path in (r'C:\Users\test\source.cpp', 'd:/users/test/source.cpp'):
        try:
            check(path.encode(encoding), 'fixture.dll')
        except ValueError:
            pass
        else:
            raise AssertionError(f'Accepted a private path encoded as {encoding}')

with tempfile.TemporaryDirectory() as tmp:
    root = Path(tmp)
    for name in ('tesseract.exe', 'tessdata/eng.traineddata', 'licenses/BUILD.txt',
                 'licenses/Leptonica.txt', 'licenses/Tesseract.txt'):
        path = root / name
        path.parent.mkdir(exist_ok=True)
        path.write_bytes(b'fixture')
    archive = root / 'WarframeCompanion-OCR-win-x64.zip'
    descriptor = root / 'descriptor.json'
    command = [sys.executable, str(tools / 'package-ocr.py'), str(root), str(archive),
               '--descriptor', str(descriptor)]
    subprocess.run(command, check=True, capture_output=True)
    before = (archive.read_bytes(), descriptor.read_bytes())
    (root / 'tesseract.exe').write_bytes(b'different fixture')
    assert subprocess.run(command, capture_output=True).returncode != 0
    assert before == (archive.read_bytes(), descriptor.read_bytes())
print('Windows build path and immutable OCR package checks passed.')
