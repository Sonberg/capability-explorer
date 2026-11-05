// Local, synthetic-input-only provider for real CLI/API acceptance; never a model service.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { createServer } from 'node:http';

const [confirmation, portText = '5133'] = process.argv.slice(2);
assert.equal(confirmation, '--synthetic-only');
const port = Number(portText);
assert.ok(Number.isInteger(port) && port > 1024 && port < 65536);
let calls = 0, inputs = 0, failNext = 0;

async function json(request) {
  const chunks = [];
  let bytes = 0;
  for await (const chunk of request) {
    bytes += chunk.length;
    if (bytes > 1_048_576) throw new RangeError('Fixture input exceeds1MiB');
    chunks.push(chunk);
  }
  return JSON.parse(Buffer.concat(chunks).toString('utf8'));
}

function respond(response, status, value) {
  response.writeHead(status, { 'content-type': 'application/json' });
  response.end(JSON.stringify(value));
}

const server = createServer(async (request, response) => {
  try {
    const url = new URL(request.url, 'http://fixture.invalid');
    if (request.method === 'GET' && url.pathname === '/health') {
      respond(response, 200, { syntheticOnly: true, calls, inputs, failNext });
      return;
    }
    if (request.method === 'PUT' && url.pathname === '/control') {
      const control = await json(request);
      assert.ok(Number.isInteger(control.failNext) && control.failNext >= 0 && control.failNext <= 10);
      failNext = control.failNext;
      respond(response, 200, { failNext });
      return;
    }
    if (request.method !== 'POST' || !url.pathname.endsWith('/embeddings')) {
      respond(response, 404, { error: { code: 'fixture-route-unavailable' } });
      return;
    }
    const body = await json(request);
    const texts = typeof body.input === 'string' ? [body.input] : body.input;
    assert.ok(Array.isArray(texts) && texts.length > 0 && texts.length <= 256);
    assert.ok(texts.every(text => typeof text === 'string' && text.length <= 200_000));
    calls++;
    inputs += texts.length;
    if (failNext > 0) {
      failNext--;
      respond(response, 400, { error: { message: 'Injected synthetic provider failure', code: 'synthetic-failure', type: 'invalid_request_error' } });
      return;
    }
    const data = texts.map((text, index) => {
      const digest = createHash('sha256').update(text).digest();
      return {
        object: 'embedding', index,
        embedding: Array.from(digest.subarray(0, 8), value => (value + 1) / 256),
      };
    });
    respond(response, 200, {
      object: 'list', model: body.model ?? 'synthetic-fixture', data,
      usage: { prompt_tokens: 0, total_tokens: 0 },
    });
  } catch (error) {
    console.error('Synthetic embedding fixture rejected request:', error.message);
    const invalid = error instanceof assert.AssertionError || error instanceof SyntaxError || error instanceof RangeError;
    if (!response.destroyed && !response.writableEnded) {
      respond(response, invalid ? 400 : 500, { error: { code: invalid ? 'invalid-fixture-request' : 'fixture-server-failure' } });
    }
  }
});

server.listen(port, '0.0.0.0', () => console.log(`Synthetic embedding fixture listening on${port}`));
for (const signal of ['SIGINT', 'SIGTERM']) {
  process.on(signal, () => server.close());
}
