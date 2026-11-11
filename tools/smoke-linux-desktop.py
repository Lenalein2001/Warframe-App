"""Run inside dbus-run-session and Xvfb with a real notification daemon."""
import json
import os
import http.client
import pathlib
import socket
import subprocess
import sys
import tempfile
import time
import urllib.request
import urllib.error

executable = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else 'src/Desktop/bin/Release/net10.0/WarframeCompanion.Desktop').resolve()
local_http = urllib.request.build_opener(urllib.request.ProxyHandler({}))
with tempfile.TemporaryDirectory(prefix='wfc-linux-') as temporary:
    root = pathlib.Path(temporary)
    with socket.socket() as listener:
        listener.bind(('127.0.0.1', 0))
        port = listener.getsockname()[1]
    def request(route, body=None):
        payload = None if body is None else json.dumps(body).encode()
        req = urllib.request.Request(f'http://127.0.0.1:{port}/api/{route}', data=payload,
                                     headers={'Content-Type': 'application/json', 'Authorization': (root / 'data' / f'api-{port}.auth').read_text().removeprefix('Authorization: ').strip() if (root / 'data' / f'api-{port}.auth').exists() else ''})
        with local_http.open(req, timeout=10) as response:
            return json.load(response)
    def wait_for(check, description, seconds=60):
        until = time.monotonic() + seconds
        while time.monotonic() < until:
            if app.poll() is not None:
                raise RuntimeError(f'Application exited: {app.returncode}; {log.read_text()}')
            try:
                if check(): return
            except (urllib.error.URLError, TimeoutError, http.client.HTTPException, ConnectionError): pass
            time.sleep(.25)
        raise RuntimeError(f'Timed out: {description}; {log.read_text()}')
    log = root / 'desktop.log'
    with log.open('w') as output, (root / 'notifications.log').open('w') as notifications:
        daemon = subprocess.Popen(['dunst'], stdout=notifications, stderr=notifications)
        app = subprocess.Popen([str(executable), '--offline', '--no-game', '--data-dir', str(root / 'data'), '--port', str(port)], stdout=output, stderr=output,
                               env={**os.environ, 'WFC_STARTUP_DIAGNOSTICS': '1'})
        try:
            wait_for(lambda: request('state').get('desktop'), 'GTK desktop startup')
            wait_for(lambda: 'Warframe App' in subprocess.check_output(['xwininfo', '-root', '-tree'], text=True), 'mapped GTK window')
            request('settings', {'screenshot_tool': 'scrot', 'overlay_backend': 'x11'})
            request('notifications/test', {})
            # Move the visible notification into history, then inspect the daemon's payload.
            subprocess.run(['dunstctl', 'close-all'], check=True)
            history = subprocess.check_output(['dunstctl', 'history'], text=True)
            assert 'Warframe Companion' in history and 'Notifications are ready' in history, history
            request('overlay/test', {})
            wait_for(lambda: 'Warframe Companion Overlay' in subprocess.check_output(['xwininfo', '-root', '-tree'], text=True), 'native X11 overlay')
            assert not any('Overlay failed' in event.get('text', '') or 'could not be created' in event.get('text', '') for event in request('state')['events'])
            request('quit', {})
            assert app.wait(timeout=20) == 0
            print('GTK window, native notifications, X11 screenshot/overlay and clean shutdown passed.')
        except Exception:
            print(log.read_text())
            print((root / 'notifications.log').read_text())
            raise
        finally:
            for process in (app, daemon):
                if process.poll() is None: process.terminate()
                try: process.wait(timeout=10)
                except subprocess.TimeoutExpired: process.kill(); process.wait()
