import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';
import test, { before } from 'node:test';
import {
  MCFluke, MCFlukeError, HostNotFoundError, Button, ReadingState,
  ConfirmationRequiredError, UnsupportedOperationError, InvalidArgumentError,
  DeviceClosedError, resolveHostPath, CLOCK_TICKS_PER_DAY,
} from '../src/index.js';

before(() => {
  try { resolveHostPath(); } catch (error) {
    if (!(error instanceof HostNotFoundError)) throw error;
    throw new Error(
      'Native offline-test prerequisite missing: build the MCFluke solution in Visual Studio 2026 '
      + '(out\\vs\\bin\\x64\\Debug or Release), or set MCFLUKE_HOST to the absolute native host path. '
      + 'Tests only use sim://187, never COM3.',
      { cause: error },
    );
  }
});

async function simulator(t) {
  const meter = await MCFluke.open('sim://187');
  t.after(() => meter.close());
  return meter;
}

test('native simulator identity, initial display, version, status and timeout ABI operations', async (t) => {
  const meter = await simulator(t);
  assert.deepEqual(await meter.getIdentity(), {
    model: 'FLUKE 187', firmware: 'SIM-1.0', serialNumber: 'SIMULATED',
  });
  assert.deepEqual(await meter.readDisplay(), {
    display: '+1.234 V DC', unit: 'V DC', state: ReadingState.VALUE, value: 1.234,
  });
  const version = await meter.getVersion();
  assert.ok(version.version);
  assert.equal(version.abi, 1);
  assert.ok(await meter.statusMessage(3));
  assert.equal(await meter.getTimeout(), 1500);
  await meter.setTimeout(50);
  assert.equal(await meter.getTimeout(), 50);
  await meter.setTimeout(60000);
  assert.equal(await meter.getTimeout(), 60000);
});

test('native parsers retain numeric units, overload, text and zero distinctly', async (t) => {
  const meter = await simulator(t);
  assert.deepEqual(await meter.parseIdentity('FLUKE 187,SIM-1.0,SIMULATED'), {
    model: 'FLUKE 187', firmware: 'SIM-1.0', serialNumber: 'SIMULATED',
  });
  assert.deepEqual(await meter.parseDisplay('QM,-12.5 mV DC'), {
    display: '-12.5 mV DC', unit: 'mV DC', state: ReadingState.VALUE, value: -12.5,
  });
  const overload = await meter.parseDisplay('QM,Out of range');
  assert.equal(overload.display, 'Out of range');
  assert.equal(overload.state, ReadingState.OUT_OF_RANGE);
  assert.equal(overload.value, null);
  const text = await meter.parseDisplay('QM,LEADS');
  assert.equal(text.display, 'LEADS');
  assert.equal(text.state, ReadingState.TEXT);
  assert.equal(text.value, null);
  const zero = await meter.parseDisplay('QM,0 V DC');
  assert.equal(zero.value, 0);
  assert.equal(zero.state, ReadingState.VALUE);
  await assert.rejects(meter.parseIdentity('not an ID'), MCFlukeError);
  await assert.rejects(meter.parseDisplay('not QM'), MCFlukeError);
});

test('native confirmation and 187-only feature guards, then simulated controls', async (t) => {
  const meter = await simulator(t);
  await assert.rejects(meter.resetInstrument(), ConfirmationRequiredError);
  await assert.rejects(meter.pressButton(Button.CALIBRATION), ConfirmationRequiredError);
  await assert.rejects(meter.pressButton(Button.LOGGING), UnsupportedOperationError);
  await assert.rejects(meter.pressButton(Button.SAVE), UnsupportedOperationError);
  await assert.rejects(meter.pressButton(999), InvalidArgumentError);
  await meter.pressButton(Button.RANGE);
  await meter.defaultSetup();
  await meter.pressButton(Button.CALIBRATION, { confirmCalibration: true });
  await meter.resetInstrument({ confirmation: true });
  assert.equal((await meter.readDisplay()).state, ReadingState.VALUE);
});

test('native operations queue in order and dispose idempotently', async (t) => {
  const meter = await simulator(t);
  const [id, reading] = await Promise.all([meter.getIdentity(), meter.readDisplay()]);
  assert.equal(id.model, 'FLUKE 187');
  assert.equal(reading.value, 1.234);
  await meter.close();
  await meter.close();
  await assert.rejects(meter.readDisplay(), DeviceClosedError);
});

test('native clock set returns written raw counter with running uint32 readback', async (t) => {
  const meter = await simulator(t);
  const initial = await meter.readClockCounter();
  assert.ok(Number.isSafeInteger(initial) && initial >= 0 && initial <= 4294967295);
  for (const ticks of [123456, 863999, 0]) {
    const written = await meter.setClock(ticks);
    assert.ok(Number.isSafeInteger(written) && written >= 0 && written <= 4294967295);
    assert.equal(written % CLOCK_TICKS_PER_DAY, ticks);
    const readback = await meter.readClockCounter();
    assert.ok(Number.isSafeInteger(readback) && readback >= 0 && readback <= 4294967295);
    const unsignedDelta = (readback - written + 4294967296) % 4294967296;
    assert.ok(unsignedDelta < 10, `clock advanced ${unsignedDelta} ticks`);
  }
  assert.equal((await meter.getIdentity()).model, 'FLUKE 187');
  assert.equal((await meter.readDisplay()).display, '+1.234 V DC');
  await meter.close();
  await assert.rejects(meter.readClockCounter(), DeviceClosedError);
  await assert.rejects(meter.setClock(0), DeviceClosedError);
});

test('native simulator reset restarts its running clock', async (t) => {
  const meter = await simulator(t);
  await meter.setClock(432000);
  await meter.resetInstrument({ confirmation: true });
  assert.ok(await meter.readClockCounter() < 10);
});

test('bounded read-only example uses the actual native simulator', async () => {
  const result = await promisify(execFile)(process.execPath, [
    fileURLToPath(new URL('../examples/read-display.js', import.meta.url)),
    '--port', 'sim://187', '--count', '2', '--interval', '0',
  ], { timeout: 15000, windowsHide: true });
  const lines = result.stdout.trim().split(/\r?\n/u).map((line) => JSON.parse(line));
  assert.equal(lines.length, 3);
  assert.equal(lines[0].identity.model, 'FLUKE 187');
  assert.equal(lines[1].display, '+1.234 V DC');
  assert.equal(lines[2].value, 1.234);
});
