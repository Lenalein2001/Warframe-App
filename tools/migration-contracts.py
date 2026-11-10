import hashlib,json,pathlib,re
root=pathlib.Path(__file__).resolve().parents[1]
s=(root/'src/server.rs').read_text()
routes={}
for method,begin,end in [('GET','fn get_api','fn post_api'),('POST','fn post_api','#[cfg(test)]')]:
    section=s[s.index(begin):s.index(end,s.index(begin))]
    routes[method]=sorted(set(re.findall(r'^        "([a-z/-]+)"\s*=>',section,re.M)) | ({'state','art'} if method=='GET' else set()))
def digest(path):
    data=path.read_bytes()
    # Git's autocrlf changes text checkouts on Windows; binary assets remain byte-exact.
    if path.suffix in {'.js','.css','.html','.txt'}:
        data=data.replace(b'\r\n',b'\n')
    return hashlib.sha256(data).hexdigest()
manifest={'baseline':'decc9b5','textHashFormat':'LF-normalized','routes':routes,'web':{p.relative_to(root).as_posix():digest(p) for p in sorted((root/'web').rglob('*')) if p.is_file()}}
target=root/'docs/migration/contracts.json'
if '--check' in __import__('sys').argv:
    assert json.loads(target.read_text())==manifest, 'Frontend or Rust API changed; review the migration contract before updating.'
    print(f'Preserved {len(manifest["web"])} frontend files and {sum(map(len,routes.values()))} API routes.')
else:
    target.write_text(json.dumps(manifest,indent=2)+'\n')
