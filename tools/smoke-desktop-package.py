"""Exercise shipped executables in isolated storage, including settings persistence."""
import json
import os
import pathlib
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import zipfile

local_http = urllib.request.build_opener(urllib.request.ProxyHandler({}))

packages = pathlib.Path(sys.argv[1]).resolve()

def request(port, route, body=None):
    payload = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(f'http://127.0.0.1:{port}/{route}', data=payload,
                                 headers={'Content-Type': 'application/json', 'Authorization': (data / f'api-{port}.auth').read_text().removeprefix('Authorization: ').strip() if (data / f'api-{port}.auth').exists() else ''})
    with local_http.open(req, timeout=5) as response:
        return json.load(response) if route.startswith('api/') else response.read()

for edition in ('framework-dependent', 'self-contained'):
    with zipfile.ZipFile(packages / (edition + '.zip')) as archive:
        names = [name.lower().replace('\\', '/') for name in archive.namelist()]
        assert not any(name.endswith(('.pdb', '.rs', '.sqlite', '.sqlite-wal', '.sqlite-shm'))
                       or name.startswith(('data/', 'artifacts/', 'webview/')) for name in names)
    with tempfile.TemporaryDirectory(prefix='wfc-package-') as temporary:
        data = pathlib.Path(temporary)
        extracted = data / 'package'
        with zipfile.ZipFile(packages / (edition + '.zip')) as archive:
            archive.extractall(extracted)
        subprocess.run([sys.executable, str(pathlib.Path(__file__).with_name('release-manifest.py')), str(extracted / 'app'), '--verify'], check=True)
        assert {p.name for p in extracted.iterdir()} == {'app', 'README.md', 'Start Warframe Companion.exe'}
        for restart in (False, True):
            with socket.socket() as listener:
                listener.bind(('127.0.0.1', 0))
                port = listener.getsockname()[1]
            executable = extracted / 'Start Warframe Companion.exe'
            with (data / 'stdout.log').open('w') as out, (data / 'stderr.log').open('w') as err:
                started = time.monotonic()
                process = subprocess.Popen([str(executable), '--headless', '--offline', '--no-game',
                                            '--data-dir', str(data), '--port', str(port)],
                                           stdout=out, stderr=err,
                                           creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
                try:
                    while True:
                        if process.poll() is not None:
                            raise RuntimeError(f'{edition} exited before startup: {process.returncode}')
                        try:
                            state = request(port, 'api/state')
                            break
                        except (urllib.error.URLError, TimeoutError):
                            if time.monotonic() - started > 60:
                                raise RuntimeError(f'{edition} did not start within 60 seconds')
                            time.sleep(.2)
                    assert state['accounts'] == [], 'Package accessed real account data'
                    assert b'<html' in request(port, ''), 'Embedded interface missing'
                    settings = request(port, 'api/settings')
                    if restart:
                        assert settings['notify'] is True and settings['hotkey_rewards'] == ''
                    else:
                        request(port, 'api/settings', {'notify': True, 'hotkey_rewards': ''})
                    # Local credentials must never be sent through configured proxies.
                    command = subprocess.run([str(executable), 'quit', '--data-dir', str(data), '--port', str(port)],
                                             env={**os.environ, 'HTTP_PROXY': 'http://127.0.0.1:1', 'ALL_PROXY': 'http://127.0.0.1:1', 'NO_PROXY': ''},
                                             capture_output=True, timeout=30, creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
                    assert command.returncode == 0, 'Authenticated desktop command failed with a proxy configured'
                    assert process.wait(timeout=15) == 0, 'Unclean application shutdown'
                    print(f'{edition}: {"restart" if restart else "first start"}, isolated data and clean shutdown passed')
                finally:
                    if process.poll() is None:
                        process.kill()
                        process.wait()
print('Both packaged editions passed.')
