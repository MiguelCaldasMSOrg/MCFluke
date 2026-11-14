import { parseArgs } from 'node:util';
import { setTimeout as sleep } from 'node:timers/promises';
import { MCFluke } from '../src/index.js';

async function main() {
  const { values } = parseArgs({
    options: {
      port: { type: 'string', default: 'COM3' },
      count: { type: 'string' },
      interval: { type: 'string', default: '2' },
      timeout: { type: 'string', default: '1500' },
    },
  });
  const count = values.count === undefined ? Infinity : Number(values.count);
  const interval = Number(values.interval);
  const timeout = Number(values.timeout);
  if ((values.count !== undefined && (!/^\d+$/u.test(values.count) || !Number.isSafeInteger(count)))
      || !Number.isFinite(interval) || interval < 0 || !Number.isInteger(timeout)) {
    throw new Error('--count must be a nonnegative integer; --interval must be finite and nonnegative; --timeout must be integer milliseconds.');
  }
  const controller = new AbortController();
  const interrupt = () => controller.abort();
  process.once('SIGINT', interrupt);
  let meter;
  try {
    meter = await MCFluke.open(values.port, { timeout });
    if (controller.signal.aborted) return;
    console.log(JSON.stringify({ identity: await meter.getIdentity() }));
    for (let index = 0; index < count && !controller.signal.aborted; index += 1) {
      if (index) await sleep(interval * 1000, undefined, { signal: controller.signal });
      if (!controller.signal.aborted) console.log(JSON.stringify(await meter.readDisplay()));
    }
  } catch (error) {
    if (!controller.signal.aborted) throw error;
  } finally {
    try { if (meter) await meter.close(); } finally {
      process.removeListener('SIGINT', interrupt);
      if (controller.signal.aborted) {
        console.error('Interrupted; the MCFluke connection has been closed.');
        process.exitCode = 130;
      }
    }
  }
}

main().catch((error) => {
  console.error(`MCFluke: ${error.message}`);
  if (error.stderr && !error.message.includes(error.stderr)) console.error(error.stderr);
  process.exitCode = 1;
});
