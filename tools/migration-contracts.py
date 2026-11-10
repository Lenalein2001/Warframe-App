"""Check preserved interface and route contracts without the retired backend."""
import hashlib, json, pathlib, re
root = pathlib.Path(__file__).resolve().parents[1]
manifest = json.loads((root / 'docs/migration/contracts.json').read_text())
def digest(path):
    data = path.read_bytes()
    if path.suffix in {'.js','.css','.html','.txt'}: data = data.replace(b'\r\n', b'\n')
    return hashlib.sha256(data).hexdigest()
actual = {p.relative_to(root).as_posix(): digest(p) for p in sorted((root/'web').rglob('*')) if p.is_file()}
assert actual == manifest['web'], 'Preserved frontend changed; review the contract.'
s = (root/'companion/Services/CompanionRuntime.cs').read_text()
g = s[s.index('public JsonNode Get('):s.index('public async Task<JsonNode> Post(')]
p = s[s.index('public async Task<JsonNode> Post('):]
routes = {'GET': set(re.findall(r'"([a-z/-]+)"\s*=>',g)) | set(re.findall(r'path == "([a-z/-]+)"',g)) | {'art'}, 'POST': set(re.findall(r'case "([a-z/-]+)":',p))}
for method, expected in manifest['routes'].items():
    missing = set(expected) - routes[method]
    assert not missing, f'Missing {method} handlers: {sorted(missing)}'
assert not list((root/'src').rglob('*.rs')), 'Retired source still present.'
print(f'Preserved {len(actual)} frontend files and {sum(map(len,manifest["routes"].values()))} API routes.')
