"""Create standard, compressed archives without executable packing or runtime extraction."""
import pathlib
import sys
import tarfile
import zipfile

source = pathlib.Path(sys.argv[1]).resolve()
destination = pathlib.Path(sys.argv[2]).resolve()
if destination.suffix == ".zip":
    with zipfile.ZipFile(destination, "x", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for path in sorted(source.rglob("*")):
            if path.is_symlink():
                raise ValueError("Unexpected symbolic link in publish output")
            if path.is_file():
                archive.write(path, path.relative_to(source).as_posix())
    sys.exit(0)

with tarfile.open(destination, "x:gz") as archive:
    for path in sorted(source.rglob("*")):
        if path.is_symlink():
            raise ValueError("Unexpected symbolic link in publish output")
        info = archive.gettarinfo(str(path), arcname=path.relative_to(source).as_posix())
        info.uid = info.gid = 0
        info.uname = info.gname = ""
        info.mode = 0o755 if path.is_dir() or path.name in ("WarframeCompanion", "WarframeCompanion.Desktop", "Portable.sh") or ".so" in path.name else 0o644
        if path.is_file():
            with path.open("rb") as stream:
                archive.addfile(info, stream)
        else:
            archive.addfile(info)
