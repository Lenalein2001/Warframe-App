import assert from 'node:assert/strict';
import { copyRelicName } from '../web/js/clipboard.js';

const written = [];
assert.equal(await copyRelicName('  Lith A13  ', { writeText: async text => written.push(text) }), true);
assert.deepEqual(written, ['Lith A13']);
assert.equal(await copyRelicName('', { writeText: async () => { throw new Error('Must not write empty text'); } }), false);
assert.equal(await copyRelicName('Axi A1', {}), false);
assert.equal(await copyRelicName('Neo N1', { writeText: async () => { throw new Error('Permission denied'); } }), false);
let invoked = false;
const pending = copyRelicName('Meso M1', { writeText: () => { invoked = true; return Promise.resolve(); } });
assert.equal(invoked, true, 'Clipboard write must start in the user click, before an async fetch');
assert.equal(await pending, true);
console.log('Relic clipboard tests passed without reading or changing the system clipboard.');
