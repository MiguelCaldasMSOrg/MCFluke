import childProcess from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const Status = Object.freeze({
  OK: 0, INVALID_ARGUMENT: 1, IO_ERROR: 2, TIMEOUT: 3, PROTOCOL_ERROR: 4,
  COMMAND_REJECTED: 5, BUFFER_TOO_SMALL: 6, WRONG_MODEL: 7,
  CONFIRMATION_REQUIRED: 8, UNSUPPORTED: 9, OUT_OF_MEMORY: 10,
});

export const Button = Object.freeze({
  BLUE: 10, HOLD: 11, MIN_MAX: 12, REL: 13, UP: 14, SHIFT: 15, HZ: 16,
  RANGE: 17, DOWN: 18, BACKLIGHT: 19, CALIBRATION: 20, AUTO_HOLD: 21,
  FAST_MIN_MAX: 22, LOGGING: 23, CANCEL: 27, WAKEUP: 28, SETUP: 29, SAVE: 30,
});

export const ReadingState = Object.freeze({ VALUE: 0, OUT_OF_RANGE: 1, TEXT: 2 });
export const CLOCK_TICKS_PER_SECOND = 10;
export const CLOCK_TICKS_PER_DAY = 864000;

export class MCFlukeError extends Error {
  constructor(message, { status, osCode = 0, operation, stderr = '' } = {}) {
    super(`${operation ? `${operation}: ` : ''}${message}`
      + `${status === undefined ? '' : ` [status=${status}, OS=${osCode}]`}`);
    this.name = new.target.name;
    this.status = status;
    this.osCode = osCode;
    this.operation = operation;
    this.stderr = stderr;
  }
}

export class NativeTimeoutError extends MCFlukeError {}
export class ConfirmationRequiredError extends MCFlukeError {}
export class UnsupportedOperationError extends MCFlukeError {}
export class WrongModelError extends MCFlukeError {}
export class InvalidArgumentError extends MCFlukeError {}
export class CommandRejectedError extends MCFlukeError {}
export class ProtocolError extends MCFlukeError {}
export class BridgeError extends MCFlukeError {}
export class DeviceClosedError extends MCFlukeError {}
export class HostNotFoundError extends MCFlukeError {}

const errorTypes = new Map([
  [Status.INVALID_ARGUMENT, InvalidArgumentError],
  [Status.TIMEOUT, NativeTimeoutError],
  [Status.PROTOCOL_ERROR, ProtocolError],
  [Status.COMMAND_REJECTED, CommandRejectedError],
  [Status.WRONG_MODEL, WrongModelError],
  [Status.CONFIRMATION_REQUIRED, ConfirmationRequiredError],
  [Status.UNSUPPORTED, UnsupportedOperationError],
]);
const MAX_LINE_BYTES = 1024 * 1024;
const MAX_STDERR_CHARACTERS = 65536;
const TOKEN = Symbol('MCFluke.open');
const isObject = (value) => value !== null && typeof value === 'object' && !Array.isArray(value);
const has = (value, key) => Object.hasOwn(value, key);

function integer(value, name, minimum = -2147483648, maximum = 2147483647) {
  if (!Number.isInteger(value) || value < minimum || value > maximum) {
    throw new InvalidArgumentError(`${name} must be an integer in ${minimum}..${maximum}`, {
      status: Status.INVALID_ARGUMENT,
    });
  }
  return value;
}

function timeoutValue(value) {
  return integer(value, 'timeout', 50, 60000);
}

function argumentText(value, name = 'argument') {
  if (typeof value !== 'string' || /[\r\n\t\0]/u.test(value)) {
    throw new InvalidArgumentError(`${name} must be a string without CR, LF, TAB or NUL`, {
      status: Status.INVALID_ARGUMENT,
    });
  }
  if (Buffer.byteLength(value, 'utf8') > MAX_LINE_BYTES - 128) {
    throw new InvalidArgumentError(`${name} exceeds the bridge line limit`, {
      status: Status.INVALID_ARGUMENT,
    });
  }
  return value;
}

function confirmation(value, name) {
  if (typeof value !== 'boolean') {
    throw new InvalidArgumentError(`${name} must be an explicit boolean`, {
      status: Status.INVALID_ARGUMENT,
    });
  }
  return value ? 1 : 0;
}

function nativeError(value, operation, stderr) {
  if (!isObject(value) || !Number.isInteger(value.status) || value.status === 0
      || !Number.isInteger(value.osCode) || typeof value.message !== 'string') {
    throw new ProtocolError('Malformed native error object', { operation, stderr });
  }
  const Type = errorTypes.get(value.status) ?? MCFlukeError;
  return new Type(value.message || 'Native operation failed', { ...value, operation, stderr });
}

function identity(value) {
  if (!isObject(value) || !['model', 'firmware', 'serialNumber']
    .every((key) => typeof value[key] === 'string')) {
    throw new ProtocolError('Malformed identity result');
  }
  return Object.freeze({
    model: value.model, firmware: value.firmware, serialNumber: value.serialNumber,
  });
}

function reading(value) {
  if (!isObject(value) || typeof value.display !== 'string' || typeof value.unit !== 'string'
      || ![0, 1, 2].includes(value.state)
      || (value.state === 0 ? !Number.isFinite(value.value) : value.value !== null)) {
    throw new ProtocolError('Malformed display result (nonnumeric states must use null)');
  }
  return Object.freeze({
    display: value.display, unit: value.unit, state: value.state, value: value.value,
  });
}

function nullResult(value) {
  if (value !== null) throw new ProtocolError('Expected null operation result');
  return null;
}

function version(value) {
  if (!isObject(value) || typeof value.version !== 'string' || value.abi !== 1) {
    throw new ProtocolError('Missing or incompatible native version (ABI 1 required)');
  }
  return Object.freeze({ version: value.version, abi: value.abi });
}

function timeoutResult(value) {
  if (!Number.isInteger(value) || value < 50 || value > 60000) {
    throw new ProtocolError('Malformed timeout result');
  }
  return value;
}

function clockCounterResult(value) {
  if (!Number.isSafeInteger(value) || value < 0 || value > 4294967295) {
    throw new ProtocolError('Malformed clock counter result (expected unsigned 32-bit integer)');
  }
  return value;
}

function stringResult(value) {
  if (typeof value !== 'string') throw new ProtocolError('Expected string result');
  return value;
}

function isFile(candidate) {
  try { return fs.statSync(candidate).isFile(); } catch { return false; }
}

export function resolveHostPath(hostPath) {
  if (process.platform !== 'win32') {
    throw new HostNotFoundError('MCFluke requires Windows.');
  }
  const override = hostPath ?? process.env.MCFLUKE_HOST;
  if (override !== undefined) {
    if (typeof override !== 'string' || !path.isAbsolute(override) || !isFile(override)) {
      throw new HostNotFoundError(
        'hostPath/MCFLUKE_HOST must be an absolute path to the native mcfluke-host executable.',
      );
    }
    return override;
  }
  const name = 'mcfluke-host.exe';
  let directory = path.dirname(fileURLToPath(import.meta.url));
  const ancestors = [];
  for (;;) {
    ancestors.push(directory);
    const parent = path.dirname(directory);
    if (parent === directory) break;
    directory = parent;
  }
  for (const ancestor of ancestors) {
    for (const configuration of ['Debug', 'Release']) {
      const candidate = path.join(ancestor, 'out', 'vs', 'bin', 'x64', configuration, name);
      if (isFile(candidate)) return candidate;
    }
  }
  for (const ancestor of ancestors) {
    const candidate = path.join(ancestor, name);
    if (isFile(candidate)) return candidate;
  }
  throw new HostNotFoundError(
    `Cannot find ${name}. Build the MCFluke solution in Visual Studio 2026 `
    + 'on Windows or set MCFLUKE_HOST to the absolute executable path. '
    + 'The host also needs the matching native library.',
  );
}

/** Persistent, serialized transport to the native core; no serial protocol lives here. */
export class MCFluke {
  static async open(port = 'COM3', { timeout = 1500, hostPath } = {}) {
    timeoutValue(timeout);
    argumentText(port, 'port');
    if (!port) {
      throw new InvalidArgumentError('port must not be empty', { status: Status.INVALID_ARGUMENT });
    }
    const executable = resolveHostPath(hostPath);
    const child = childProcess.spawn(executable, ['--port', port, '--timeout', String(timeout)], {
      shell: false, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'],
    });
    const client = new MCFluke(TOKEN, child, timeout);
    try {
      await client._ready;
      return client;
    } catch (error) {
      await client._terminate();
      error.stderr = client.diagnostics;
      if (error.stderr && !error.message.includes(error.stderr)) {
        error.message += `\nNative host stderr:\n${error.stderr}`;
      }
      throw error;
    }
  }

  constructor(token, child, timeout) {
    if (token !== TOKEN) throw new TypeError('Use await MCFluke.open(port, options).');
    this._child = child;
    this._timeout = timeout;
    this._pending = new Map();
    this._nextId = 1;
    this._queue = Promise.resolve();
    this._failure = null;
    this._closing = false;
    this._exited = false;
    this._closeAcknowledged = false;
    this._readySettled = false;
    this._buffer = '';
    this._stderr = '';
    this._decoder = new TextDecoder('utf-8', { fatal: true });
    this._stderrDecoder = new TextDecoder();
    this._exit = new Promise((resolve) => { this._resolveExit = resolve; });
    this._ready = new Promise((resolve, reject) => {
      this._resolveReady = resolve;
      this._rejectReady = reject;
    });
    this._startupTimer = setTimeout(() => {
      this._fatal(new BridgeError('Native host did not become ready before the startup deadline'));
    }, timeout * 2 + 1000);
    child.stdout.on('data', (chunk) => this._data(chunk));
    child.stdout.on('error', (error) => this._fatal(new BridgeError(`Host stdout: ${error.message}`)));
    child.stderr.on('data', (chunk) => {
      this._stderr = (this._stderr + this._stderrDecoder.decode(chunk, { stream: true }))
        .slice(-MAX_STDERR_CHARACTERS);
    });
    child.stderr.on('error', (error) => {
      this._stderr = `${this._stderr}\nUnable to read host stderr: ${error.message}`
        .slice(-MAX_STDERR_CHARACTERS);
    });
    child.stdin.on('error', (error) => {
      if (!this._closeAcknowledged) this._fatal(new BridgeError(`Host stdin: ${error.message}`));
    });
    child.on('error', (error) => this._fatal(new BridgeError(`Cannot start native host: ${error.message}`)));
    child.once('close', (code, signal) => this._onClose(code, signal));
  }

  get diagnostics() { return this._stderr; }
  get closed() { return this._closing || this._exited || this._failure !== null; }

  _data(chunk) {
    if (this._failure) return;
    try {
      this._buffer += this._decoder.decode(chunk, { stream: true });
      let end;
      while ((end = this._buffer.indexOf('\n')) !== -1) {
        const line = this._buffer.slice(0, end).replace(/\r$/u, '');
        this._buffer = this._buffer.slice(end + 1);
        if (Buffer.byteLength(line, 'utf8') > MAX_LINE_BYTES) {
          throw new ProtocolError('Native host response exceeds the line limit');
        }
        this._line(line);
        if (this._failure) return;
      }
      if (Buffer.byteLength(this._buffer, 'utf8') > MAX_LINE_BYTES) {
        throw new ProtocolError('Native host response exceeds the line limit');
      }
    } catch (error) {
      this._fatal(error instanceof MCFlukeError
        ? error : new ProtocolError(`Invalid native host JSON/UTF-8: ${error.message}`));
    }
  }

  _line(line) {
    const message = JSON.parse(line);
    if (!isObject(message) || typeof message.ok !== 'boolean') {
      throw new ProtocolError('Malformed native host response');
    }
    if (!this._readySettled) {
      if (message.type !== 'ready') throw new ProtocolError('Expected native host ready handshake');
      if (!message.ok) throw nativeError(message.error, 'open', this.diagnostics);
      this._readySettled = true;
      clearTimeout(this._startupTimer);
      this._resolveReady();
      return;
    }
    if (!Number.isSafeInteger(message.id) || message.id < 1 || !this._pending.has(message.id)) {
      throw new ProtocolError('Unexpected native host response ID');
    }
    const pending = this._pending.get(message.id);
    if (!message.ok) {
      const error = nativeError(message.error, pending.verb, this.diagnostics);
      this._pending.delete(message.id);
      clearTimeout(pending.timer);
      pending.reject(error);
      return;
    }
    if (!has(message, 'result')) throw new ProtocolError('Native host response is missing result');
    const result = pending.validate(message.result);
    this._pending.delete(message.id);
    clearTimeout(pending.timer);
    if (pending.verb === 'close') this._closeAcknowledged = true;
    pending.resolve(result);
  }

  _onClose(code, signal) {
    this._exited = true;
    this._stderr = (this._stderr + this._stderrDecoder.decode()).slice(-MAX_STDERR_CHARACTERS);
    this._resolveExit();
    if (this._closeAcknowledged && this._pending.size === 0 && code === 0 && !this._buffer) return;
    const details = this.diagnostics ? `\nNative host stderr:\n${this.diagnostics}` : '';
    this._fatal(new BridgeError(
      `Native host exited unexpectedly (code=${code}, signal=${signal ?? 'none'})${details}`,
      { stderr: this.diagnostics },
    ), false);
  }

  _fatal(error, terminate = true) {
    if (this._failure) return;
    error.stderr = this.diagnostics;
    this._failure = error;
    clearTimeout(this._startupTimer);
    if (!this._readySettled) {
      this._readySettled = true;
      this._rejectReady(error);
    }
    for (const pending of this._pending.values()) {
      clearTimeout(pending.timer);
      pending.reject(error);
    }
    this._pending.clear();
    if (terminate) void this._terminate();
  }

  _waitForExit(milliseconds) {
    if (this._exited) return Promise.resolve(true);
    return new Promise((resolve) => {
      const timer = setTimeout(() => resolve(false), milliseconds);
      this._exit.then(() => { clearTimeout(timer); resolve(true); });
    });
  }

  _terminate() {
    if (!this._termination) {
      this._termination = (async () => {
        if (this._exited) return;
        this._child.stdin.destroy();
        this._child.kill();
        if (!await this._waitForExit(500)) {
          this._child.kill('SIGKILL');
          await this._waitForExit(500);
        }
      })();
    }
    return this._termination;
  }

  _request(verb, argument = '', validate = nullResult) {
    if (this._failure) return Promise.reject(this._failure);
    if (this._exited) return Promise.reject(new DeviceClosedError('The native host is closed.'));
    if (!Number.isSafeInteger(this._nextId)) {
      const error = new BridgeError('Native bridge request IDs exhausted; reopen the connection.');
      this._fatal(error);
      return Promise.reject(error);
    }
    const id = this._nextId++;
    return new Promise((resolve, reject) => {
      // Recomputed when each serialized operation starts, including after setTimeout.
      // Conservative bridge watchdog; native QS + PS still share one deadline.
      const timeoutMultiplier = verb === 'set-clock' ? 4 : 2;
      const timer = setTimeout(() => this._fatal(new BridgeError(
        `${verb}: native host exceeded its response deadline (native timeout ${this._timeout} ms)`,
        { operation: verb, stderr: this.diagnostics },
      )), this._timeout * timeoutMultiplier + 1000);
      this._pending.set(id, { resolve, reject, timer, verb, validate });
      this._child.stdin.write(`${id}\t${verb}\t${argument}\n`, 'utf8', (error) => {
        if (error) this._fatal(new BridgeError(`Cannot write to native host: ${error.message}`));
      });
    });
  }

  _enqueue(verb, argument = '', validate = nullResult, after) {
    if (this._closing) return Promise.reject(new DeviceClosedError('The MCFluke client is closing/closed.'));
    if (this._failure) return Promise.reject(this._failure);
    const operation = this._queue.then(async () => {
      const result = await this._request(verb, argument, validate);
      if (after) after(result);
      return result;
    });
    this._queue = operation.catch(() => {});
    return operation;
  }

  async getIdentity() { return this._enqueue('id', '', identity); }
  async readDisplay() { return this._enqueue('display', '', reading); }
  async readClockCounter() { return this._enqueue('clock', '', clockCounterResult); }
  async setClock(timeOfDayTicks) {
    const ticks = integer(timeOfDayTicks, 'timeOfDayTicks', 0, CLOCK_TICKS_PER_DAY - 1);
    return this._enqueue('set-clock', String(ticks), clockCounterResult);
  }
  async pressButton(button, { confirmCalibration = false } = {}) {
    return this._enqueue('button', `${integer(button, 'button')} ${confirmation(confirmCalibration, 'confirmCalibration')}`);
  }
  async defaultSetup() { return this._enqueue('defaults'); }
  async resetInstrument({ confirmation: confirmed = false } = {}) {
    return this._enqueue('reset', String(confirmation(confirmed, 'confirmation')));
  }
  async getTimeout() {
    return this._enqueue('get-timeout', '', timeoutResult, (value) => { this._timeout = value; });
  }
  async setTimeout(timeout) {
    timeoutValue(timeout);
    return this._enqueue('set-timeout', String(timeout), nullResult, () => { this._timeout = timeout; });
  }
  async parseIdentity(response) { return this._enqueue('parse-id', argumentText(response), identity); }
  async parseDisplay(response) { return this._enqueue('parse-display', argumentText(response), reading); }
  async getVersion() { return this._enqueue('version', '', version); }
  async statusMessage(status) { return this._enqueue('status-message', String(integer(status, 'status')), stringResult); }

  close() {
    if (this._closePromise) return this._closePromise;
    this._closing = true;
    this._closePromise = this._queue.then(async () => {
      try {
        if (this._failure || this._exited) {
          await this._terminate();
          return;
        }
        await this._request('close');
        if (!await this._waitForExit(1000)) {
          throw new BridgeError('Native host acknowledged close but did not exit.');
        }
        if (this._failure) throw this._failure;
      } finally {
        await this._terminate();
      }
    });
    this._queue = this._closePromise.catch(() => {});
    return this._closePromise;
  }

  async [Symbol.asyncDispose]() { await this.close(); }
}
