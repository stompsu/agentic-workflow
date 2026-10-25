import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

// The backend's golden SSE files are the byte-level reference for the wire format (read-only).
// Tests run with cwd = src/frontend.
const DIR = resolve(process.cwd(), '../backend/tests/SupportChat.Api.Tests/Golden');

export function golden(name: 'answer' | 'clarification' | 'error-midstream'): string {
  return readFileSync(resolve(DIR, `${name}.sse`), 'utf8')
    .replaceAll('{SESSION_ID}', '0192f0c1-7a3e-7cc1-9a51-3f1e2b9d4c10')
    .replaceAll('{MESSAGE_ID}', '0192f0c1-bbbb-7000-8000-000000000001');
}
