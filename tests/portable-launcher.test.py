"""Exercise POSIX portable profile selection with a fake executable, never the UI."""
import os
import pathlib
import shutil
import shlex
import subprocess
import tempfile

shell = shutil.which("bash") or r"C:/Program Files/Git/bin/bash.exe"
source = pathlib.Path(__file__).resolve().parents[1] / "packaging/linux/Portable.sh"
with tempfile.TemporaryDirectory(prefix="wfc-portable-") as temp:
    root = pathlib.Path(temp) / "Space and Unicode é"
    (root / "app").mkdir(parents=True)
    shutil.copy2(source, root / "Portable.sh")
    probe = root / "app/WarframeCompanion.Desktop"
    probe.write_text("""#!/bin/sh
printf '%s\\0' "$@" > probe.args
exit 23
""", encoding="utf-8")
    probe.chmod(0o755)
    def run(args, code=23):
        result = subprocess.run([shell], input=("exec sh ./Portable.sh " + shlex.join(args)).encode("utf-8"), cwd=root, timeout=15,
                                env={**os.environ, "MSYS_NO_PATHCONV": "1"}, capture_output=True)
        assert result.returncode == code, result.stderr.decode()
        if code != 23:
            return []
        data = (root / "probe.args").read_bytes().decode("utf-8").split("\0")[:-1]
        return [] if data == [""] else data
    assert run([]) == []
    portable = run(["--portable", "--offline"])
    assert portable[0] == "--data-dir" and portable[1].endswith("/data") and portable[2:] == ["--offline"]
    (root / "data").mkdir()
    for name in ("state.sqlite", "companion.sqlite"):
        database = root / "data" / name
        database.touch()
        assert run([]) == portable[:2]
        assert run(["--data-dir", "/tmp/custom profile", "literal\"quote"]) == ["--data-dir", "/tmp/custom profile", "literal\"quote"]
        database.unlink()
    run(["--portable", "--data-dir", "/tmp/other"], 2)
    assert run(["--data-dir", "--portable"]) == ["--data-dir", "--portable"]
print("POSIX launcher profile selection, quoting, conflict handling and exit codes passed.")
