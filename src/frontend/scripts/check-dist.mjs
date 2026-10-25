// Post-build guard: the e2e auth bypass must not exist in a production bundle.
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';

const MARKER = 'E2E_MOCK_AUTH_ACTIVE';

function* walk(dir) {
  for (const entry of readdirSync(dir)) {
    const p = join(dir, entry);
    if (statSync(p).isDirectory()) yield* walk(p);
    else yield p;
  }
}

const offenders = [...walk('dist')].filter((f) => /\.(js|html|map)$/.test(f) && readFileSync(f, 'utf8').includes(MARKER));
if (offenders.length > 0) {
  console.error(`Build contains the e2e mock-auth marker (${MARKER}):\n${offenders.join('\n')}`);
  process.exit(1);
}
console.log('check-dist: no e2e mock-auth code in the bundle');
