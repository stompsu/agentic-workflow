// Self-test for check-dist.mjs: node scripts/check-dist.test.mjs
import { spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import assert from 'node:assert/strict';

const script = join(import.meta.dirname, 'check-dist.mjs');
const root = mkdtempSync(join(tmpdir(), 'check-dist-'));

function fixture(name, files) {
  const dir = join(root, name);
  mkdirSync(join(dir, 'assets'), { recursive: true });
  for (const [f, content] of Object.entries(files)) writeFileSync(join(dir, f), content);
  return dir;
}
const run = (dir) => spawnSync(process.execPath, dir === undefined ? [script] : [script, dir], { encoding: 'utf8', cwd: root });

try {
  assert.equal(run(fixture('clean', { 'index.html': '<html></html>', 'assets/a.js': 'console.log(1)' })).status, 0, 'clean dir passes');
  const cases = {
    e2e: { 'assets/a.js': 'x="E2E_MOCK_AUTH_ACTIVE"' },
    marker: { 'assets/a.js': 'x="LOCAL_DEV_AUTH_ACTIVE"' },
    marker_html: { 'index.html': '<p>LOCAL_DEV_AUTH_ACTIVE</p>' },
    path: { 'assets/a.js': 'fetch("/local-auth/token")' },
    file: { 'config.local.json': '{}' },
  };
  for (const [name, files] of Object.entries(cases)) {
    const r = run(fixture(name, files));
    assert.equal(r.status, 1, `${name} must fail`);
    assert.match(r.stderr, /test\/local-only code/);
  }
  assert.equal(run(join(root, 'missing')).status, 2, 'missing dir is an error');
  console.log('check-dist self-test: ok');
} finally {
  rmSync(root, { recursive: true, force: true });
}
