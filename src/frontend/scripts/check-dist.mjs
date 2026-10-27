// Post-build guard: neither the e2e auth bypass nor the local-mode sign-in may exist in a production bundle.
// Usage: node scripts/check-dist.mjs [dir]   (default: dist)
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';

const MARKERS = [
  { text: 'E2E_MOCK_AUTH_ACTIVE', what: 'e2e mock-auth marker' },
  { text: 'LOCAL_DEV_AUTH_ACTIVE', what: 'local-mode auth marker' },
  { text: '/local-auth/', what: 'local token endpoint path' },
];
const FORBIDDEN_FILES = ['config.local.json'];

function* walk(dir) {
  for (const entry of readdirSync(dir)) {
    const p = join(dir, entry);
    if (statSync(p).isDirectory()) yield* walk(p);
    else yield p;
  }
}

const dir = process.argv[2] ?? 'dist';
if (!existsSync(dir) || !statSync(dir).isDirectory()) {
  console.error(`check-dist: directory not found: ${dir}`);
  process.exit(2);
}

const problems = [];
for (const file of walk(dir)) {
  const rel = relative(dir, file);
  if (FORBIDDEN_FILES.includes(file.split(/[\\/]/).pop())) problems.push(`${rel}: forbidden file`);
  if (!/\.(js|mjs|html|css|json|map)$/.test(file)) continue;
  const content = readFileSync(file, 'utf8');
  for (const m of MARKERS) if (content.includes(m.text)) problems.push(`${rel}: ${m.what} (${m.text})`);
}

if (problems.length > 0) {
  console.error(`check-dist: ${dir} contains test/local-only code:\n${problems.join('\n')}`);
  process.exit(1);
}
console.log(`check-dist: no e2e mock-auth or local-mode code in ${dir}`);
