import { defineConfig, devices } from '@playwright/test';

const PORT = 4173;

// The web server runs Vite in `--mode e2e`: that is the ONLY way __E2E_MOCK_AUTH__ becomes true (MSAL is replaced
// by a mock provider). `vite build` refuses this mode and scripts/check-dist.mjs scans the production bundle.
// Browsers are installed separately (`npx playwright install chromium`); this repo does not do it automatically.
export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : 'list',
  use: {
    baseURL: `http://localhost:${PORT}`,
    trace: 'retain-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: {
    command: `npx vite --mode e2e --port ${PORT} --strictPort`,
    url: `http://localhost:${PORT}/config.json`,
    reuseExistingServer: !process.env.CI,
    timeout: 60_000,
  },
});
