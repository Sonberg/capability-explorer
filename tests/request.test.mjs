import test from 'node:test';
import assert from 'node:assert/strict';
import { request } from '../platform/src/CapabilityExplorer.Explorer/client/src/api.ts';

test('JSON request headers remain valid for mixed-case, Headers and tuple inputs', async () => {
  const original = globalThis.fetch;
  const received = [];
  globalThis.fetch = async (_url, init) => {
    received.push(new Headers(init.headers));
    return new Response('{"ok":true}', { status: 200 });
  };
  try {
    for (const headers of [
      { 'Content-Type': 'application/json', 'X-CE-Binding-Preview': '1' },
      new Headers({ 'Content-Type': 'application/json' }),
      [['Content-Type', 'application/json']],
    ]) assert.deepEqual(await request('/preview', { method: 'POST', body: '{}', headers }), { ok: true });
    assert.equal(received.length, 3);
    for (const headers of received) {
      assert.equal(headers.get('content-type'), 'application/json');
      assert.equal(headers.get('accept'), 'application/json');
    }
    assert.equal(received[0].get('x-ce-binding-preview'), '1');
  } finally { globalThis.fetch = original; }
});
