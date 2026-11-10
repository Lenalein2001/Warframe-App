"""Produce usable Linux permissions even when cross-publishing on Windows."""
import pathlib
import sys
import tarfile

source = pathlib.Path(sys.argv[1]).resolve()
destination = pathlib.Path(sys.argv[2]).resolve()
with tarfile.open(destination, "x:gz") as archive:
    for path in sorted(source.rglob("*")):
        if path.is_symlink():
            raise ValueError("Unexpected symbolic link in publish output")
        info = archive.gettarinfo(str(path), arcname=path.relative_to(source).as_posix())
        info.uid = info.gid = 0
        info.uname = info.gname = ""
        info.mode = 0o755 if path.is_dir() or path.name == "WarframeCompanion" or ".so" in path.name else 0o644
        if path.is_file():
            with path.open("rb") as stream:
                archive.addfile(info, stream)
        else:
            archive.addfile(info)
