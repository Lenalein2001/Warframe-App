"""Release manifests reject corruption and private runtime files."""
import pathlib
import subprocess
import sys
import tempfile

script = pathlib.Path(__file__).resolve().parents[1] / 'tools/release-manifest.py'
with tempfile.TemporaryDirectory(prefix='wfc-manifest-') as temporary:
    root = pathlib.Path(temporary)
    (root / 'Host.dll').write_bytes(b'synthetic assembly')
    (root / 'Microsoft.Data.Sqlite.dll').write_bytes(b'fixture')
    (root / 'Microsoft.AspNetCore.Authentication.dll').write_bytes(b'fixture')
    def run(*args, success=True):
        result = subprocess.run([sys.executable, str(script), str(root), *args], capture_output=True)
        assert (result.returncode == 0) == success, result.stderr.decode()
    run('--runtime', 'fixture', '--edition', 'fixture')
    run('--verify')
    (root / 'Host.dll').write_bytes(b'changed assembly')
    run('--verify', success=False)
    (root / 'Host.dll').write_bytes(b'synthetic assembly')
    for name in ('api-8642.auth', 'state.sqlite-wal', 'debug.pdb'):
        forbidden = root / name
        forbidden.write_bytes(b'private fixture')
        run(success=False)
        forbidden.unlink()
    (root / 'Host.dll').unlink()
    run('--verify', success=False)
print('Release manifest integrity and private-file rejection passed.')
