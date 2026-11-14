import { EventEmitter } from 'node:events';
import { PassThrough, Writable } from 'node:stream';

export const IDENTITY = Object.freeze({
  model: 'FLUKE 187', firmware: 'SIM-1.0', serialNumber: 'SIMULATED',
});
export const READING = Object.freeze({
  display: '+1.234 V DC', unit: 'V DC', state: 0, value: 1.234,
});

/** Scripted bridge bytes for transport tests only; never opens serial ports. */
export class ChildFixture extends EventEmitter {
  constructor(handler, { ready = 'normal' } = {}) {
    super();
    this.stdout = new PassThrough();
    this.stderr = new PassThrough();
    this.requests = [];
    this.kills = [];
    this.timeout = 1500;
    this.timers = new Set();
    this.ended = false;
    let input = '';
    this.stdin = new Writable({
      write: (chunk, encoding, callback) => {
        input += chunk.toString('utf8');
        let end;
        while ((end = input.indexOf('\n')) !== -1) {
          const line = input.slice(0, end);
          input = input.slice(end + 1);
          const [id, verb, argument, ...extra] = line.split('\t');
          const request = { id: Number(id), verb, argument, extra, line };
          this.requests.push(request);
          queueMicrotask(() => {
            if (this.ended) return;
            try {
              const handled = handler?.(request, this);
              if (!handled) this.defaultReply(request);
            } catch (error) {
              this.emit('error', error);
            }
          });
        }
        callback();
      },
    });
    queueMicrotask(() => {
      if (this.ended) return;
      if (typeof ready === 'function') ready(this);
      else if (ready === 'chunked') this.sendChunks({ type: 'ready', ok: true });
      else if (ready === 'normal') this.send({ type: 'ready', ok: true });
    });
  }

  send(message) {
    this.stdout.write(`${JSON.stringify(message)}\n`);
  }

  sendChunks(message) {
    const bytes = Buffer.from(`${JSON.stringify(message)}\n`, 'utf8');
    for (let index = 0; index < bytes.length; index += 1) this.stdout.write(bytes.subarray(index, index + 1));
  }

  reply(request, result) {
    this.send({ id: request.id, ok: true, result });
  }

  reject(request, status, message = 'fixture native failure') {
    this.send({ id: request.id, ok: false, error: { status, osCode: 123, message } });
  }

  later(milliseconds, callback) {
    const timer = setTimeout(() => {
      this.timers.delete(timer);
      if (!this.ended) callback();
    }, milliseconds);
    this.timers.add(timer);
  }

  defaultReply(request) {
    switch (request.verb) {
      case 'id': this.reply(request, IDENTITY); break;
      case 'display': this.reply(request, READING); break;
      case 'get-timeout': this.reply(request, this.timeout); break;
      case 'set-timeout':
        this.timeout = Number(request.argument);
        this.reply(request, null);
        break;
      case 'version': this.reply(request, { version: 'fixture-1', abi: 1 }); break;
      case 'status-message': this.reply(request, 'fixture status'); break;
      case 'close':
        this.reply(request, null);
        queueMicrotask(() => this.finish(0));
        break;
      default: this.reply(request, null); break;
    }
  }

  finish(code, signal = null) {
    if (this.ended) return;
    this.ended = true;
    for (const timer of this.timers) clearTimeout(timer);
    this.timers.clear();
    this.stdout.end();
    this.stderr.end();
    queueMicrotask(() => this.emit('close', code, signal));
  }

  kill(signal = 'SIGTERM') {
    this.kills.push(signal);
    this.finish(null, signal);
    return true;
  }
}
