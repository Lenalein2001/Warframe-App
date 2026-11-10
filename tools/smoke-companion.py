"""Exercise the published CLI with synthetic data in an isolated copy of its directory."""
import json
import pathlib
import shutil
import subprocess
import sys
import tempfile

source = pathlib.Path(sys.argv[1]).resolve()
with tempfile.TemporaryDirectory(prefix="companion-smoke-") as temporary:
    root = pathlib.Path(temporary)
    app = root / "app"
    shutil.copytree(source, app)
    exe = app / ("WarframeCompanion.exe" if sys.platform == "win32" else "WarframeCompanion")
    fixture = root / "inventory.json"

    def run(*args, code=0):
        result = subprocess.run([str(exe), *map(str, args)], capture_output=True, text=True, timeout=30, cwd=root)
        assert result.returncode == code, (result.returncode, result.stderr)
        return json.loads(result.stdout) if result.stdout.strip() else None

    fixture.write_text(json.dumps({"MiscItems": [{"ItemType": "/Synthetic", "ItemCount": 3}], "LastInventorySync": 100}))
    assert run("validate", "--file", fixture, "--account", "sample")["counts"]["/Synthetic"] == 3
    assert not (app / "data").exists(), "Validation must not create storage"
    assert run("import", "--file", fixture, "--account", "sample", "--complete", "--portable")["promoted"]
    assert (app / "data" / "companion.sqlite").is_file(), "Portable data must be next to executable"
    fixture.write_text('{"MiscItems": [], "LastInventorySync": 200}')
    assert not run("import", "--file", fixture, "--account", "sample", "--portable")["promoted"]
    assert run("current", "--account", "sample", "--portable")["counts"]["/Synthetic"] == 3
    assert len(run("history", "--account", "sample", "--portable")) == 2
    export = root / "snapshot.json"
    run("export", "--account", "sample", "--output", export, "--portable")
    run("export", "--account", "sample", "--output", export, "--portable", code=1)
    assert json.loads(export.read_text())["schemaVersion"] == 1
    fixture.write_text('{"MiscItems": {}}')
    run("import", "--file", fixture, "--account", "sample", "--complete", "--portable", code=2)
    assert len(run("history", "--account", "sample", "--portable")) == 2
    run("current", "--account", "missing", "--portable", code=2)
    blocked = root / "not-a-directory"
    blocked.write_text("file")
    run("check", "--data-dir", blocked, code=1)
    backup = root / "backup.sqlite"
    run("backup", "--output", backup, "--portable")
    assert run("check", "--portable")["integrity"] == "ok"
print("Published CLI smoke passed: import, isolation, validation, history, export, backup, integrity and portable paths.")
