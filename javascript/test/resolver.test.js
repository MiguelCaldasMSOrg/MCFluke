import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const directory = fileURLToPath(new URL('..', import.meta.url));
const moduleUrl = new URL('../src/index.js', import.meta.url).href;

function checkResolver({ available, expected, override, failure }) {
  const environment = { ...process.env };
  delete environment.MCFLUKE_HOST;
  const script = `
    import assert from 'node:assert/strict';
    import fs from 'node:fs';
    import path from 'node:path';
    import { resolveHostPath, HostNotFoundError } from ${JSON.stringify(moduleUrl)};
    const root = path.resolve('..');
    const name = process.platform === 'win32' ? 'mcfluke-host.exe' : 'mcfluke-host';
    const locations = {
      debug: path.join(root, 'out', 'vs', 'bin', 'x64', 'Debug', name),
      release: path.join(root, 'out', 'vs', 'bin', 'x64', 'Release', name),
      legacy: path.join(root, 'build', 'bin', name),
      nearbyLegacy: path.join(root, 'javascript', 'src', 'build', 'bin', name),
      adjacent: path.join(root, 'javascript', 'src', name),
      explicit: path.join(root, 'javascript', 'fixtures', name),
    };
    const available = new Set(${JSON.stringify(available)}.map(key => locations[key]));
    const attempts = [];
    fs.statSync = candidate => {
      attempts.push(candidate);
      if (!available.has(candidate)) throw Object.assign(new Error('fixture missing file'), { code: 'ENOENT' });
      return { isFile: () => true };
    };
    const override = locations[${JSON.stringify(override)}];
    ${failure
      ? `assert.throws(() => resolveHostPath(override), error => error instanceof HostNotFoundError && new RegExp(${JSON.stringify(failure.source)}, ${JSON.stringify(failure.flags)}).test(error.message));`
      : `assert.equal(resolveHostPath(override), locations[${JSON.stringify(expected)}]);`}
    ${override ? 'assert.equal(attempts.length, 1);' : ''}
  `;
  return execFileSync(process.execPath, ['--input-type=module', '-e', script], {
    cwd: directory, env: environment, encoding: 'utf8', timeout: 10000, windowsHide: true,
  });
}

test('Visual Studio Debug output precedes Release and nearer legacy/adjacent hosts', () => {
  checkResolver({
    available: ['debug', 'release', 'legacy', 'nearbyLegacy', 'adjacent'], expected: 'debug',
  });
});

test('Visual Studio Release output is selected when Debug is absent', () => {
  checkResolver({ available: ['release', 'legacy', 'adjacent'], expected: 'release' });
});

test('legacy host discovery remains available on every supported platform', () => {
  checkResolver({ available: ['legacy'], expected: 'legacy' });
});

test('explicit hostPath remains authoritative over Visual Studio discovery', () => {
  checkResolver({ available: ['debug', 'explicit'], expected: 'explicit', override: 'explicit' });
});

test('missing explicit hostPath never falls back to an existing Visual Studio host', () => {
  checkResolver({ available: ['debug'], override: 'explicit', failure: /absolute path/u });
});

test('missing native prerequisite reports the Visual Studio build workflow', () => {
  checkResolver({ available: [], failure: /Visual Studio 2026/u });
});

test('MCFLUKE_HOST remains authoritative without mutating the parent environment', () => {
  const environment = { ...process.env, MCFLUKE_HOST: process.execPath };
  const script = `
    import assert from 'node:assert/strict';
    import { resolveHostPath } from ${JSON.stringify(moduleUrl)};
    assert.equal(resolveHostPath(), process.execPath);
  `;
  const result = execFileSync(process.execPath, ['--input-type=module', '-e', script], {
    cwd: directory, env: environment, encoding: 'utf8', timeout: 10000, windowsHide: true,
  });
  assert.equal(result, '');
});
