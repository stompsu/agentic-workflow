#!/usr/bin/env node
// Runs the whole app locally without Azure (docs/adr/0005-local-mode.md section 6):
//   node scripts/run-local.mjs [--models scripted|foundry] [--auth local|entra]
// Starts the local-only backend (http://localhost:5080), waits for /healthz, then the Vite dev server in local mode
// (http://localhost:5173). Ctrl+C stops both. No secrets here: Foundry / Entra settings come from user-secrets.
import { spawn, spawnSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const frontend = join(root, 'src', 'frontend');
const backendProject = join(root, 'src', 'backend', 'src', 'SupportChat.LocalHost');
const backendUrl = 'http://localhost:5080';
const isWindows = process.platform === 'win32';

function fail(message) {
  console.error(`run-local: ${message}`);
  process.exit(1);
}

function option(name, allowed) {
  const index = process.argv.indexOf(`--${name}`);
  if (index < 0) return undefined;
  const value = (process.argv[index + 1] ?? '').toLowerCase();
  if (!allowed.includes(value)) fail(`--${name} must be one of: ${allowed.join(', ')}`);
  return value;
}

const known = new Set(['--models', '--auth']);
for (let i = 2; i < process.argv.length; i += 1) {
  const arg = process.argv[i];
  if (arg.startsWith('--') && !known.has(arg)) fail(`unknown option ${arg}`);
}
const models = option('models', ['scripted', 'foundry']);
const auth = option('auth', ['local', 'entra']);

// Prerequisites.
const dotnet = spawnSync('dotnet', ['--version'], { encoding: 'utf8' });
if (dotnet.status !== 0 || Number.parseInt(dotnet.stdout, 10) < 10) fail('the .NET 10 SDK is required (dotnet --version).');
if (Number.parseInt(process.versions.node, 10) < 24) fail(`Node >= 24 is required (found ${process.versions.node}).`);

if (!existsSync(join(frontend, 'node_modules'))) {
  console.log('run-local: installing frontend dependencies (npm ci)...');
  const install = spawnSync('npm', ['ci'], { cwd: frontend, stdio: 'inherit', shell: isWindows });
  if (install.status !== 0) fail('npm ci failed.');
}

const children = [];
let stopping = false;

function stop(code = 0) {
  if (stopping) return;
  stopping = true;
  for (const child of children) {
    if (child.exitCode === null) child.kill('SIGINT');
  }
  setTimeout(() => process.exit(code), 3000).unref();
  Promise.all(children.map((c) => new Promise((resolve) => (c.exitCode !== null ? resolve() : c.once('exit', resolve))))).then(() => process.exit(code));
}

process.on('SIGINT', () => stop(0));
process.on('SIGTERM', () => stop(0));

function start(name, command, args, options) {
  const child = spawn(command, args, { stdio: 'inherit', shell: isWindows, ...options });
  children.push(child);
  child.on('exit', (code) => {
    if (!stopping) {
      console.error(`run-local: ${name} exited (code ${code}); stopping.`);
      stop(code ?? 1);
    }
  });
  return child;
}

const env = { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development' };
if (models) env.LocalMode__Models = models;
if (auth) env.LocalMode__Auth = auth;

console.log('run-local: starting the backend (dotnet run)...');
start('backend', 'dotnet', ['run', '--project', backendProject], { env });

async function waitForBackend() {
  const deadline = Date.now() + 180_000;
  while (Date.now() < deadline && !stopping) {
    try {
      const response = await fetch(`${backendUrl}/healthz`);
      if (response.ok) return true;
    } catch {
      // not up yet
    }
    await new Promise((resolve) => setTimeout(resolve, 500));
  }
  return false;
}

if (!(await waitForBackend())) {
  if (!stopping) fail(`the backend did not become healthy at ${backendUrl}/healthz within 180 s.`);
} else {
  console.log('run-local: backend is healthy; starting the frontend...');
  // Local sign-in only exists with the local issuer; with --auth entra the SPA uses plain `dev` (MSAL), which proxies /api
  // to BACKEND_URL (default http://localhost:5080).
  const script = auth === 'entra' ? 'dev' : 'dev:local';
  start('frontend', 'npm', ['--prefix', frontend, 'run', script], { env: { ...process.env, BACKEND_URL: backendUrl } });
  console.log('\nrun-local: open http://localhost:5173  (Ctrl+C to stop)\n');
}
