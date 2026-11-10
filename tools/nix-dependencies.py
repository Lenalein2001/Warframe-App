"""Pin NuGet archives from a completed locked restore for Nix builds."""
import hashlib, json, os, pathlib
root = pathlib.Path(__file__).resolve().parents[1]
cache = pathlib.Path(os.environ.get('NUGET_PACKAGES', pathlib.Path.home()/'.nuget/packages'))
packages = {}
for lock in list((root/'src').glob('*/packages.lock.json')) + list((root/'tests').glob('*/packages.lock.json')):
    for framework in json.loads(lock.read_text())['dependencies'].values():
        for name, item in framework.items():
            if item['type'].lower() == 'project': continue
            version = item['resolved']
            archive = cache/name.lower()/version/f'{name.lower()}.{version}.nupkg'
            packages[(name,version)] = {'pname':name, 'version':version, 'sha256':hashlib.sha256(archive.read_bytes()).hexdigest(), 'url':f'https://api.nuget.org/v3-flatcontainer/{name.lower()}/{version}/{name.lower()}.{version}.nupkg'}
(root/'nix/deps.json').write_text(json.dumps([packages[k] for k in sorted(packages)],indent=2)+'\n')
print(f'Pinned {len(packages)} NuGet archives.')
