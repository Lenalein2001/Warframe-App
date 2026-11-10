"""Regenerate the unchanged stat constants from the preserved Rust reference."""
import json
import pathlib
import re

root = pathlib.Path(__file__).resolve().parents[1]
source = (root / 'src/rivens.rs').read_text(encoding='utf-8')
stats = []
for name, unit, bases, sign, spliced in re.findall(r'Stat \{ name: "([^"]+)", unit: Unit::(\w+), base: \[([^\]]+)\], sign: Sign::(\w+), spliced: (true|false) \}', source):
    values = [float(v[5:-1]) if v.startswith('Some(') else None for v in bases.split(', ')]
    stats.append(dict(name=name, unit={'Percent':'%', 'Multiplier':'%', 'Metres':'m', 'Seconds':'s', 'Count':''}[unit], bases=values, sign=sign, spliced=spliced=='true'))
assert len(stats) == 50
wording = dict(re.findall(r'\("([^"]+)", "([^"]+)"\)', source.split('pub static WORDING:')[1].split('];')[0]))
slugs = dict(re.findall(r'"([^"]+)" => "([^"]+)"', source.split('pub fn market_slug')[1].split('/// (bonus')[0]))
(root / 'companion/Services/riven-data.json').write_text(json.dumps(dict(stats=stats, wording=wording, slugs=slugs), indent=2)+'\n')
