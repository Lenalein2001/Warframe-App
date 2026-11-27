"""Headless single-file checks with empty extraction caches and isolated settings."""
import ctypes
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import urllib.request

executable = Path(sys.argv[1]).resolve()
report = Path(sys.argv[2]).resolve()
http = urllib.request.build_opener(urllib.request.ProxyHandler({}))
results = []
with tempfile.TemporaryDirectory(prefix='wfc-single-file-') as temporary:
    root = Path(temporary)
    application = root / 'portable'
    application.mkdir()
    shutil.copy2(executable, application / executable.name)
    exe = application / executable.name
    data = application / 'data'
    extraction = root / 'extraction'
    env = {**os.environ, 'DOTNET_BUNDLE_EXTRACT_BASE_DIR': str(extraction)}
    for run in range(3):
        if run == 2:
            shutil.rmtree(extraction)  # Re-extraction after cache cleanup must also work.
        with socket.socket() as reservation:
            reservation.bind(('127.0.0.1', 0))
            port = reservation.getsockname()[1]
        def request(route, body=None):
            credential = (data / f'api-{port}.auth').read_text().removeprefix('Authorization: ').strip()
            req = urllib.request.Request(f'http://127.0.0.1:{port}/{route}',
                data=None if body is None else json.dumps(body).encode(),
                headers={'Authorization': credential, 'Content-Type': 'application/json'})
            with http.open(req, timeout=5) as response:
                return json.load(response) if route.startswith('api/') else response.read()
        started = time.monotonic()
        process = subprocess.Popen([str(exe), '--headless', '--offline', '--no-game', *(['--portable'] if run == 0 else []), '--port', str(port)],
            env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=subprocess.CREATE_NO_WINDOW)
        spawn_seconds = time.monotonic() - started
        try:
            while True:
                if process.poll() is not None:
                    raise RuntimeError(f'App exited: {process.returncode}')
                try:
                    state = request('api/state')
                    break
                except (OSError, TimeoutError):
                    if time.monotonic() - started > 60:
                        raise RuntimeError('Startup timed out')
                    time.sleep(.05)
            elapsed = time.monotonic() - started
            assert state['accounts'] == []
            assert state['version'] == (Path(__file__).resolve().parents[1] / 'VERSION').read_text().strip()
            assert b'<html' in request('')
            assert request('licenses/LICENSE.txt')
            settings = request('api/settings')
            if run:
                assert settings['notify'] is True
            else:
                request('api/settings', {'notify': True})
            assert not (data / 'components').exists(), 'OCR downloaded without permission'
            assert {p.name for p in application.iterdir()} == {exe.name, 'data'}, 'Sidecar files appeared next to executable'
            native = list(extraction.rglob('*.dll'))
            names = {p.name for p in native}
            assert {'warframe_native.dll', 'e_sqlite3.dll', 'Photino.Native.dll', 'WebView2Loader.dll', 'libSkiaSharp.dll'} <= names
            assert 'coreclr.dll' not in names
            if run == 0:
                # Resolve native UI dependencies without opening or creating a window.
                for name in ['Photino.Native.dll', 'WebView2Loader.dll']:
                    library = ctypes.WinDLL(str(next(p for p in native if p.name == name)))
                    ctypes.windll.kernel32.FreeLibrary(ctypes.c_void_p(library._handle))
            results.append({'run': run, 'empty_extraction_cache': run != 1,
                'headless_ready_seconds': round(elapsed, 3), 'process_creation_seconds': round(spawn_seconds, 3),
                'extracted_bytes': sum(p.stat().st_size for p in extraction.rglob('*') if p.is_file()),
                'stages': (data / 'startup-timing.log').read_text().splitlines()})
            report.parent.mkdir(parents=True, exist_ok=True)
            report.write_text(json.dumps(results, indent=2))
            quit = subprocess.run([str(exe), 'quit', '--port', str(port)], env=env, capture_output=True, timeout=15, creationflags=subprocess.CREATE_NO_WINDOW)
            assert quit.returncode == 0, 'Command did not select adjacent portable data'
            assert process.wait(timeout=15) == 0
            print(f'Run {run}: {elapsed:.3f}s; empty extraction cache={run != 1}', flush=True)
        finally:
            if process.poll() is None:
                process.kill()
                process.wait()
print('Single-file package, dependencies, isolated settings and restart checks passed.')
