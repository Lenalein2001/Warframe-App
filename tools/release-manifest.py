"""Record and verify release contents without machine paths or private runtime data."""
import argparse
import hashlib
import json
import pathlib
import subprocess

NAME = 'build-manifest.json'


def inventory(root):
    result = {}
    for path in sorted(root.rglob('*')):
        if path.is_symlink():
            raise ValueError('Release contains a symbolic link')
        if not path.is_file() or path == root / NAME:
            continue
        relative = path.relative_to(root).as_posix()
        if any(part in ('.git', 'data', 'webview', 'artifacts', '__pycache__') for part in path.relative_to(root).parts) or (path.name.startswith('api-') and '.auth' in path.name) or path.suffix.lower() in ('.sqlite', '.sqlite-wal', '.sqlite-shm', '.sqlite-journal', '.auth', '.pdb', '.rs', '.pyc'):
            raise ValueError('Release contains private or development files: ' + relative)
        with path.open('rb') as stream:
            result[relative] = {'bytes': path.stat().st_size, 'sha256': hashlib.file_digest(stream, 'sha256').hexdigest()}
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=pathlib.Path)
    parser.add_argument('--verify', action='store_true')
    parser.add_argument('--runtime')
    parser.add_argument('--edition')
    args = parser.parse_args()
    root = args.directory.resolve()
    if not root.is_dir():
        raise ValueError('Package directory is missing')
    files = inventory(root)
    if args.verify:
        manifest = json.loads((root / NAME).read_text(encoding='utf-8'))
        if manifest.get('schema') != 1 or manifest.get('files') != files:
            raise ValueError('Package contents do not match the build manifest')
        print(f'Verified {len(files)} package files')
        return
    repository = pathlib.Path(__file__).resolve().parent.parent
    commit = subprocess.check_output(['git', '-C', str(repository), 'rev-parse', 'HEAD'], text=True).strip()
    dirty = bool(subprocess.check_output(['git', '-C', str(repository), 'status', '--porcelain', '--untracked-files=no'], text=True).strip())
    manifest = {'schema': 1, 'repository': 'https://github.com/Lenalein2001/Warframe-App',
                'commit': commit, 'uncommittedChanges': dirty, 'runtime': args.runtime, 'edition': args.edition, 'files': files}
    (root / NAME).write_text(json.dumps(manifest, indent=2, sort_keys=True) + '\n', encoding='utf-8')


if __name__ == '__main__':
    main()
