/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { resolve } from 'node:path';

// Local backend dev port: src/backend/src/AgenticWorkflow.Api/Properties/launchSettings.json
const BACKEND = process.env.BACKEND_URL ?? 'http://localhost:5080';

export default defineConfig(({ command, mode }) => {
  // __E2E_MOCK_AUTH__ is a compile-time constant: true ONLY for `vite --mode e2e` (the Playwright web server).
  // A production build with that mode is refused, and scripts/check-dist.mjs greps the bundle after `vite build`.
  if (command === 'build' && mode === 'e2e') {
    throw new Error('The e2e mock-auth mode cannot be used with `vite build`.');
  }
  return {
    plugins: [react()],
    define: { __E2E_MOCK_AUTH__: JSON.stringify(command === 'serve' && mode === 'e2e') },
    build: {
      target: 'es2023',
      rollupOptions: {
        input: { main: resolve(import.meta.dirname, 'index.html'), redirect: resolve(import.meta.dirname, 'redirect.html') },
      },
    },
    server: {
      port: 5173,
      proxy: {
        '/api': { target: BACKEND, changeOrigin: false, ws: false },
      },
    },
    test: {
      environment: 'jsdom',
      setupFiles: ['src/test/setup.ts'],
      include: ['src/**/*.test.{ts,tsx}'],
      restoreMocks: true,
    },
  };
});
