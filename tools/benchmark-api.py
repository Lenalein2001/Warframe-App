"""Measure local API latency without exporting account data or changing settings."""
import argparse
import json
import pathlib
import statistics
import time
import urllib.parse
import urllib.request

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--url', default='http://127.0.0.1:8642')
parser.add_argument('--rounds', type=int, default=30)
parser.add_argument('--output', type=pathlib.Path)
args = parser.parse_args()
url = urllib.parse.urlsplit(args.url)
if url.scheme != 'http' or url.hostname not in ('127.0.0.1', 'localhost') or url.username:
    parser.error('Use a local companion HTTP address.')
if not 5 <= args.rounds <= 1000:
    parser.error('Choose 5–1000 rounds.')
result = {}
for route in ('state', 'inventory', 'mastery', 'primes', 'relics'):
    timings = []
    for sample in range(args.rounds + 3):
        start = time.perf_counter()
        with urllib.request.urlopen(args.url.rstrip('/') + '/api/' + route, timeout=30) as response:
            body = response.read()
        elapsed = (time.perf_counter() - start) * 1000
        json.loads(body)
        if sample >= 3:
            timings.append(elapsed)
    timings.sort()
    result[route] = {'median_ms': round(statistics.median(timings), 2),
                     'p95_ms': round(timings[int((len(timings) - 1) * .95)], 2),
                     'response_bytes': len(body), 'samples': len(timings)}
text = json.dumps(result, indent=2) + '\n'
if args.output:
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(text, encoding='utf-8')
print(text, end='')
