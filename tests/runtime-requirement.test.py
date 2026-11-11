"""Exercise the native apphost missing-runtime path without showing its GUI dialog."""
import os
import pathlib
import subprocess
import sys
import tempfile

executable = pathlib.Path(sys.argv[1]).resolve()
with tempfile.TemporaryDirectory(prefix="wfc-no-runtime-") as temporary:
    root = pathlib.Path(temporary)
    isolated = root / "empty-dotnet"
    isolated.mkdir()
    result = subprocess.run([str(executable), "--headless", "--offline", "--no-game", "--data-dir", str(root / "profile")],
        env={**os.environ, "DOTNET_ROOT_X64": str(isolated), "DOTNET_ROOT": str(isolated),
             "DOTNET_MULTILEVEL_LOOKUP": "0", "DOTNET_DISABLE_GUI_ERRORS": "1"},
        capture_output=True, timeout=15, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    diagnostic = (result.stdout + result.stderr).decode("utf-8", errors="replace")
    assert result.returncode != 0, "Application ran without the required shared runtime"
    assert "https://aka.ms/dotnet" in diagnostic, diagnostic
    assert "x64" in diagnostic, diagnostic
    assert not (root / "profile").exists(), "Managed code started despite missing runtime"
print("Missing-runtime native host error includes an x64 download link; no dialog or profile was opened.")
