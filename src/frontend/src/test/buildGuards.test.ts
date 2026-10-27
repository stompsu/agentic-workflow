import { spawnSync } from 'node:child_process';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

const root = resolve(import.meta.dirname, '../..');
const vite = resolve(root, 'node_modules/vite/bin/vite.js');

function run(args: string[], script = vite, timeout = 60_000) {
  return spawnSync(process.execPath, [script, ...args], { cwd: root, encoding: 'utf8', timeout });
}

describe('local-mode guards (G8)', () => {
  it('vite build --mode localdev throws and writes nothing', () => {
    const r = run(['build', '--mode', 'localdev', '--outDir', 'node_modules/.tmp-localdev-guard']);
    expect(r.status).not.toBe(0);
    expect(r.stderr + r.stdout).toMatch(/cannot be used with `vite build`/);
  }, 90_000);

  it('vite --mode localdev --host refuses to start', () => {
    const r = run(['--mode', 'localdev', '--host']);
    expect(r.status).not.toBe(0);
    expect(r.stderr + r.stdout).toMatch(/localhost only/);
  }, 90_000);

  it('vite build --mode e2e still throws', () => {
    const r = run(['build', '--mode', 'e2e', '--outDir', 'node_modules/.tmp-e2e-guard']);
    expect(r.status).not.toBe(0);
  }, 90_000);
});

describe('check-dist self-test', () => {
  it('passes', () => {
    const r = spawnSync(process.execPath, [resolve(root, 'scripts/check-dist.test.mjs')], { cwd: root, encoding: 'utf8' });
    expect(r.stderr).toBe('');
    expect(r.status).toBe(0);
    expect(r.stdout).toContain('ok');
  });
});
