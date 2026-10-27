import { defineConfig, devices } from '@playwright/test';

// MANUAL local-mode suite (ADR 0005 section 7, decision L7: not in CI). `npm run e2e:local`.
// Starts the real LocalHost backend (scripted model, in-memory stores, local ES256 issuer, port 5080) and
// `vite --mode localdev` (port 5173, the only Origin the token endpoint accepts). Needs the .NET 10 SDK and
// browsers (`npx playwright install chromium`). Stop any running `node scripts/run-local.mjs` first (same ports).
const FRONTEND = 'http://localhost:5173';

export default defineConfig({
  testDir: './e2e-local',
  fullyParallel: false,
  workers: 1,
  forbidOnly: !!process.env.CI,
  retries: 0,
  reporter: 'list',
  timeout: 60_000,
  use: { baseURL: FRONTEND, trace: 'retain-on-failure' },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: [
    {
      command: 'dotnet run --project ../backend/src/SupportChat.LocalHost',
      url: 'http://localhost:5080/healthz',
      reuseExistingServer: true,
      timeout: 180_000,
      stdout: 'pipe',
      stderr: 'pipe',
    },
    {
      command: 'npm run dev:local',
      url: `${FRONTEND}/config.json`,
      reuseExistingServer: true,
      timeout: 60_000,
    },
  ],
});
