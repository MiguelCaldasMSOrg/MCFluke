import assert from 'node:assert/strict';
import childProcess from 'node:child_process';
import path from 'node:path';
import test from 'node:test';
import {
  MCFluke, MCFlukeError, BridgeError, ProtocolError, HostNotFoundError,
  DeviceClosedError, InvalidArgumentError, NativeTimeoutError,
  ConfirmationRequiredError, UnsupportedOperationError, WrongModelError,
  CommandRejectedError, Button, ReadingState, resolveHostPath,
} from '../src/index.js';
import { ChildFixture, IDENTITY, READING } from '../fixtures/child-fixture.js';

function fixture(t, handler, options) {
  const child = new ChildFixture(handler, options);
  const calls = [];
  t.mock.method(childProcess, 'spawn', (...arguments_) => {
    calls.push(arguments_);
    return child;
  });
  t.after(() => { if (!child.ended) child.kill(); });
  return { child, calls };
}

async function open(t, handler, { ready, timeout = 50 } = {}) {
  const { child, calls } = fixture(t, handler, { ready });
  const meter = await MCFluke.open('sim://187', { timeout, hostPath: process.execPath });
  t.after(async () => { await meter.close().catch(() => {}); });
  return { meter, child, calls };
}

test('lazy import and explicit host resolution reject bad overrides', async () => {
  assert.throws(() => new MCFluke(), /MCFluke.open/u);
  assert.throws(() => resolveHostPath('relative-host'), HostNotFoundError);
  assert.throws(() => resolveHostPath(path.resolve('does-not-exist-fixture-host')), HostNotFoundError);
  assert.equal(resolveHostPath(process.execPath), process.execPath);
  await assert.rejects(MCFluke.open('sim://187', { timeout: 0 }), InvalidArgumentError);
  await assert.rejects(MCFluke.open('sim://187', { timeout: 2 ** 32 + 1500 }), InvalidArgumentError);
  await assert.rejects(MCFluke.open('sim://187\nCOM3'), InvalidArgumentError);
});

test('one shell-free persistent child, exact argv and protocol lines', async (t) => {
  const { meter, child, calls } = await open(t);
  assert.deepEqual(await meter.getIdentity(), IDENTITY);
  assert.deepEqual(await meter.readDisplay(), READING);
  await meter.pressButton(Button.RANGE);
  await meter.pressButton(Button.CALIBRATION, { confirmCalibration: true });
  await meter.defaultSetup();
  await meter.resetInstrument();
  await meter.resetInstrument({ confirmation: true });
  assert.equal(await meter.getTimeout(), 1500);
  await meter.setTimeout(60000);
  assert.equal(await meter.getTimeout(), 60000);
  assert.deepEqual(await meter.getVersion(), { version: 'fixture-1', abi: 1 });
  assert.equal(await meter.statusMessage(3), 'fixture status');
  await meter.close();
  assert.equal(calls.length, 1);
  assert.deepEqual(calls[0], [
    process.execPath, ['--port', 'sim://187', '--timeout', '50'],
    { shell: false, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] },
  ]);
  assert.deepEqual(child.requests.slice(0, 7).map(({ line }) => line), [
    '1\tid\t', '2\tdisplay\t', '3\tbutton\t17 0', '4\tbutton\t20 1',
    '5\tdefaults\t', '6\treset\t0', '7\treset\t1',
  ]);
  assert.ok(child.requests.every((request) => request.id > 0 && request.extra.length === 0));
  assert.equal(child.kills.length, 0);
  assert.equal(meter.closed, true);
});

test('chunked ready and single-byte UTF-8/JSON responses', async (t) => {
  const expected = { ...IDENTITY, serialNumber: 'SIMULATÉD-Δ' };
  const { meter } = await open(t, (request, child) => {
    if (request.verb !== 'id') return false;
    child.sendChunks({ id: request.id, ok: true, result: expected });
    return true;
  }, { ready: 'chunked' });
  assert.deepEqual(await meter.getIdentity(), expected);
});

test('multiple complete lines in a chunk and CRLF work without losing frames', async (t) => {
  const { meter } = await open(t, null, {
    ready: (child) => child.stdout.write('{"type":"ready","ok":true}\r\n'),
  });
  assert.deepEqual(await meter.readDisplay(), READING);
});

test('all calls are serialized and accepted work drains before close', async (t) => {
  let active = 0;
  let maximum = 0;
  const { meter, child } = await open(t, (request, process_) => {
    if (request.verb === 'close') return false;
    active += 1;
    maximum = Math.max(maximum, active);
    process_.later(10, () => {
      active -= 1;
      process_.defaultReply(request);
    });
    return true;
  });
  const first = meter.getIdentity();
  const second = meter.readDisplay();
  const closing = meter.close();
  await assert.rejects(meter.getIdentity(), DeviceClosedError);
  assert.strictEqual(meter.close(), closing);
  assert.deepEqual(await first, IDENTITY);
  assert.deepEqual(await second, READING);
  await closing;
  assert.equal(maximum, 1);
  assert.deepEqual(child.requests.map(({ verb }) => verb), ['id', 'display', 'close']);
});

test('parser calls delegate unchanged text; nonnumeric display is null', async (t) => {
  const { meter, child } = await open(t, (request, process_) => {
    if (request.verb === 'parse-id') process_.reply(request, IDENTITY);
    else if (request.verb === 'parse-display') {
      process_.reply(request, { display: 'OL V DC', unit: 'V DC', state: 1, value: null });
    } else return false;
    return true;
  });
  assert.deepEqual(await meter.parseIdentity('FLUKE 187,SIM-1.0,SIMULATED'), IDENTITY);
  const result = await meter.parseDisplay('QM,OL V DC');
  assert.equal(result.state, ReadingState.OUT_OF_RANGE);
  assert.equal(result.value, null);
  assert.equal(result.display, 'OL V DC');
  assert.equal(child.requests[1].argument, 'QM,OL V DC');
});

test('argument delimiters, invalid integers and implicit confirmations are rejected locally', async (t) => {
  const { meter, child } = await open(t);
  for (const text of ['bad\npayload', 'bad\rpayload', 'bad\tpayload', 'bad\0payload', 123]) {
    await assert.rejects(meter.parseIdentity(text), InvalidArgumentError);
    await assert.rejects(meter.parseDisplay(text), InvalidArgumentError);
  }
  await assert.rejects(meter.parseDisplay('x'.repeat(1024 * 1024)), InvalidArgumentError);
  for (const timeout of [49, 60001, true, '1500', NaN, 1.5]) {
    await assert.rejects(meter.setTimeout(timeout), InvalidArgumentError);
  }
  await assert.rejects(meter.pressButton(2 ** 32 + 17), InvalidArgumentError);
  await assert.rejects(meter.pressButton(Button.CALIBRATION, { confirmCalibration: 1 }), InvalidArgumentError);
  await assert.rejects(meter.resetInstrument({ confirmation: 1 }), InvalidArgumentError);
  await assert.rejects(meter.statusMessage(1.5), InvalidArgumentError);
  assert.equal(child.requests.length, 0);
});

test('structured native errors retain codes and do not break a healthy connection', async (t) => {
  const errors = [
    [1, InvalidArgumentError], [2, MCFlukeError], [3, NativeTimeoutError],
    [4, ProtocolError], [5, CommandRejectedError], [7, WrongModelError],
    [8, ConfirmationRequiredError], [9, UnsupportedOperationError], [10, MCFlukeError],
  ];
  let current = 0;
  const { meter } = await open(t, (request, child) => {
    if (request.verb !== 'display' || current >= errors.length) return false;
    child.stderr.write('fixture stderr\n');
    child.reject(request, errors[current++][0], 'explicit native timeout/error');
    return true;
  });
  for (const [status, Type] of errors) {
    await assert.rejects(meter.readDisplay(), (error) => {
      assert.ok(error instanceof Type);
      assert.equal(error.status, status);
      assert.equal(error.osCode, 123);
      assert.equal(error.operation, 'display');
      assert.match(error.message, /explicit native timeout\/error/u);
      assert.match(error.stderr, /fixture stderr/u);
      return true;
    });
  }
  assert.deepEqual(await meter.readDisplay(), READING);
});

test('startup failure includes native error and stderr and kills child', async (t) => {
  const { child } = fixture(t, null, {
    ready: (process_) => {
      process_.stderr.write('verified identity failed\n');
      process_.send({ type: 'ready', ok: false, error: { status: 7, osCode: 11, message: 'not FLUKE 187' } });
    },
  });
  await assert.rejects(
    MCFluke.open('sim://187', { timeout: 50, hostPath: process.execPath }),
    (error) => {
      assert.ok(error instanceof WrongModelError);
      assert.equal(error.osCode, 11);
      assert.match(error.stderr, /verified identity failed/u);
      return true;
    },
  );
  assert.ok(child.ended);
  assert.equal(child.kills.length, 1);
});

test('spawn errors reject startup without unhandled rejections', async (t) => {
  const { child } = fixture(t, null, {
    ready: (process_) => process_.emit('error', new Error('fixture ENOENT')),
  });
  await assert.rejects(
    MCFluke.open('sim://187', { timeout: 50, hostPath: process.execPath }),
    /fixture ENOENT/u,
  );
  assert.ok(child.ended);
});

test('malformed startup never leaves a child running', async (t) => {
  for (const content of ['not json\n', '{"ok":true}\n', '{"type":"ready","ok":0}\n']) {
    await t.test(JSON.stringify(content), async (subtest) => {
      const { child } = fixture(subtest, null, { ready: (process_) => process_.stdout.write(content) });
      await assert.rejects(
        MCFluke.open('sim://187', { timeout: 50, hostPath: process.execPath }), ProtocolError,
      );
      assert.ok(child.ended);
    });
  }
});

test('malformed replies reject pending and queued work and terminate transport', async (t) => {
  const cases = [
    ['broken JSON', (id) => '{broken\n'],
    ['unknown ID', (id) => `${JSON.stringify({ id: id + 1, ok: true, result: IDENTITY })}\n`],
    ['missing result', (id) => `${JSON.stringify({ id, ok: true })}\n`],
    ['invalid identity', (id) => `${JSON.stringify({ id, ok: true, result: {} })}\n`],
    ['malformed error', (id) => `${JSON.stringify({ id, ok: false, error: { status: 3 } })}\n`],
    ['false zero-status error', (id) => `${JSON.stringify({ id, ok: false, error: { status: 0, osCode: 0, message: '' } })}\n`],
    ['invalid UTF-8', () => Buffer.from([0xff, 0x0a])],
    ['oversized line', () => 'x'.repeat(1024 * 1024 + 1)],
  ];
  for (const [name, content] of cases) {
    await t.test(name, async (subtest) => {
      const { meter, child } = await open(subtest, (request, process_) => {
        process_.stdout.write(content(request.id));
        return true;
      });
      const first = assert.rejects(meter.getIdentity(), ProtocolError);
      const second = assert.rejects(meter.readDisplay(), ProtocolError);
      await Promise.all([first, second]);
      await meter.close();
      assert.ok(child.ended);
      assert.equal(child.requests.length, 1);
    });
  }
});

test('malformed display cannot disguise nonnumeric states as zero', async (t) => {
  const { meter } = await open(t, (request, child) => {
    child.reply(request, { display: 'OL', unit: '', state: 1, value: 0 });
    return true;
  });
  await assert.rejects(meter.readDisplay(), ProtocolError);
});

test('unexpected exit rejects pending and queued calls with stderr', async (t) => {
  const { meter, child } = await open(t, (request, process_) => {
    process_.stderr.write('fixture child crashed\n');
    process_.finish(23);
    return true;
  });
  await Promise.all([
    assert.rejects(meter.getIdentity(), /child crashed/u),
    assert.rejects(meter.readDisplay(), BridgeError),
  ]);
  assert.equal(child.requests.length, 1);
  await meter.close();
  assert.equal(meter.closed, true);
});

test('exit before ready and truncated response reject without waiting for deadline', async (t) => {
  const { child } = fixture(t, null, {
    ready: (process_) => {
      process_.stdout.write('{"type":"ready"');
      process_.finish(19);
    },
  });
  await assert.rejects(
    MCFluke.open('sim://187', { timeout: 50, hostPath: process.execPath }),
    /code=19/u,
  );
  assert.ok(child.ended);
});

test('startup deadline is bounded and closes a silent helper', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const { child } = fixture(t, null, { ready: 'silent' });
  const rejected = assert.rejects(
    MCFluke.open('sim://187', { timeout: 50, hostPath: process.execPath }),
    /startup deadline/u,
  );
  t.mock.timers.tick(1101);
  await rejected;
  assert.ok(child.ended);
});

test('pending deadline uses the successfully changed timeout, not the original', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const { meter, child } = await open(t, (request) => request.verb === 'display', { timeout: 60000 });
  await meter.setTimeout(50);
  const rejected = assert.rejects(meter.readDisplay(), /native timeout 50 ms/u);
  await Promise.resolve();
  await Promise.resolve();
  t.mock.timers.tick(1101);
  await rejected;
  assert.ok(child.ended);
  await meter.close();
});

test('queued timeout increase takes effect before a following request deadline', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  let pending;
  const { meter, child } = await open(t, (request) => {
    if (request.verb !== 'display') return false;
    pending = request;
    return true;
  });
  const setting = meter.setTimeout(2000);
  const reading = meter.readDisplay();
  await setting;
  for (let index = 0; index < 5; index += 1) await Promise.resolve();
  assert.ok(pending);
  t.mock.timers.tick(1200);
  assert.equal(meter.closed, false);
  child.reply(pending, READING);
  assert.deepEqual(await reading, READING);
  await meter.close();
});

test('stderr diagnostics remain bounded and available after close', async (t) => {
  const { meter, child } = await open(t);
  child.stderr.write(`${'x'.repeat(100000)}TAIL`);
  assert.equal(meter.diagnostics.length, 65536);
  assert.ok(meter.diagnostics.endsWith('TAIL'));
  await meter[Symbol.asyncDispose]();
  assert.ok(meter.diagnostics.endsWith('TAIL'));
  await assert.rejects(meter.getVersion(), DeviceClosedError);
});

test('two instances have independent queues and lifetimes', async (t) => {
  const children = [];
  t.mock.method(childProcess, 'spawn', () => {
    const child = new ChildFixture();
    children.push(child);
    return child;
  });
  const options = { timeout: 50, hostPath: process.execPath };
  const first = await MCFluke.open('sim://187', options);
  const second = await MCFluke.open('sim://187', options);
  t.after(async () => { await Promise.all([first.close(), second.close()]); });
  await first.close();
  assert.deepEqual(await second.getIdentity(), IDENTITY);
  assert.equal(children.length, 2);
  assert.equal(second.closed, false);
});
