"""Non-visual launcher regression: use a GUI-subsystem probe, never the real UI."""
import pathlib
import shutil
import struct
import subprocess
import sys
import tempfile

build = pathlib.Path(sys.argv[1]).resolve()
launcher = build / 'companion_launcher.exe'
binary = launcher.read_bytes()
pe = struct.unpack_from('<I', binary, 0x3c)[0]
assert struct.unpack_from('<H', binary, pe + 24 + 68)[0] == 2, 'Launcher opens a console'
with tempfile.TemporaryDirectory(prefix='wfc-launch-') as temp:
    root = pathlib.Path(temp) / 'Space and Unicode é'
    (root / 'app').mkdir(parents=True)
    shutil.copy2(launcher, root / 'Start Warframe Companion.exe')
    shutil.copy2(build / 'launcher_probe.exe', root / 'app/WarframeCompanion.Desktop.exe')
    def run(args, code=23):
        result = subprocess.run([str(root / 'Start Warframe Companion.exe'), *args],
                                cwd=temp, timeout=15, creationflags=subprocess.CREATE_NO_WINDOW)
        assert result.returncode == code, 'Child exit code was lost'
        if code != 23:
            return []
        lines = (root / 'probe.txt').read_text(encoding='utf-16-le').splitlines()
        assert lines.pop(0) == 'no-console'
        return lines
    assert run([]) == [], 'Fresh package isolated the installed profile'
    assert run(['--portable']) == ['--data-dir', str(root / 'data')]
    (root / 'data').mkdir()
    (root / 'data/state.sqlite').touch()
    assert run([]) == ['--data-dir', str(root / 'data')], 'Existing portable storage ignored'
    (root / 'data/state.sqlite').unlink()
    (root / 'data/companion.sqlite').touch()
    assert run([]) == ['--data-dir', str(root / 'data')], 'Inventory-only portable storage ignored'
    run(['--portable', '--data-dir', 'custom'], 87)
    assert run(['--data-dir', '--portable']) == ['--data-dir', '--portable']
    explicit = 'C:\\path with space\\'
    assert run(['--data-dir', explicit, 'literal"quote']) == ['--data-dir', explicit, 'literal"quote']
print('Native launcher: no console, profile selection, Unicode paths, argument quoting and exit codes passed.')
