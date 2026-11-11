"""Check archive layout, payload integrity and Linux executable permissions."""
import pathlib
import subprocess
import sys
import tarfile
import tempfile
import zipfile

script = pathlib.Path(__file__).resolve().parents[1] / "tools/archive-companion.py"
with tempfile.TemporaryDirectory(prefix="wfc-archive-") as temporary:
    root = pathlib.Path(temporary)
    source = root / "source"
    (source / "app").mkdir(parents=True)
    payload = b"portable fixture\n" * 1000
    (source / "app/WarframeCompanion.Desktop").write_bytes(payload)
    (source / "Portable.sh").write_text("#!/bin/sh\n", encoding="utf-8")
    for suffix in (".zip", ".tar.gz"):
        target = root / ("package" + suffix)
        subprocess.run([sys.executable, str(script), str(source), str(target)], check=True)
        if suffix == ".zip":
            with zipfile.ZipFile(target) as archive:
                assert archive.testzip() is None
                assert archive.read("app/WarframeCompanion.Desktop") == payload
                assert set(archive.namelist()) == {"Portable.sh", "app/WarframeCompanion.Desktop"}
        else:
            with tarfile.open(target) as archive:
                assert archive.extractfile("app/WarframeCompanion.Desktop").read() == payload
                for name in ("Portable.sh", "app/WarframeCompanion.Desktop"):
                    assert archive.getmember(name).mode == 0o755
        assert target.stat().st_size < len(payload)
        repeat = subprocess.run([sys.executable, str(script), str(source), str(target)], capture_output=True)
        assert repeat.returncode != 0, "Existing release silently overwritten"
print("Archive layout, integrity, compression and Linux executable permissions passed.")
