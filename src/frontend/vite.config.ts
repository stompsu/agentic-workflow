/// <reference types="vitest/config" />
import { defineConfig, type Plugin } from 'vite';
import react from '@vitejs/plugin-react';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

// Local backend dev port: src/backend/src/SupportChat.Api/Properties/launchSettings.json
const BACKEND = process.env.BACKEND_URL ?? 'http://localhost:5080';

const LOCAL_MODE = 'localdev';
const LOCAL_PORT = 5173;

// Local mode (ADR 0005 section 5), dev server only: answers /config.json from config.local.json (kept outside public/ so it
// never reaches dist/) and refuses to start when the resolved host is not `localhost` (e.g. `--host`).
function localDevPlugin(): Plugin {
  return {
    name: 'support-local-dev',
    apply: 'serve',
    configResolved(resolved) {
      if (resolved.server.host !== 'localhost') {
        throw new Error('Local mode (--mode localdev) must listen on localhost only; remove --host.');
      }
    },
    configureServer(server) {
      // Registered directly (not in a returned post-hook) so it runs before Vite's public/static middleware.
      server.middlewares.use((req, res, next) => {
        if (req.url?.split('?')[0] !== '/config.json') return next();
        try {
          const body = readFileSync(resolve(import.meta.dirname, 'config.local.json'), 'utf8');
          res.setHeader('Content-Type', 'application/json; charset=utf-8');
          res.setHeader('Cache-Control', 'no-store');
          res.end(body);
        } catch (e) {
          next(e);
        }
      });
    },
  };
}

export default defineConfig(({ command, mode }) => {
  // __E2E_MOCK_AUTH__ is a compile-time constant: true ONLY for `vite --mode e2e` (the Playwright web server).
  // A production build with that mode is refused, and scripts/check-dist.mjs greps the bundle after `vite build`.
  if (command === 'build' && mode === 'e2e') {
    throw new Error('The e2e mock-auth mode cannot be used with `vite build`.');
  }
  // __LOCAL_AUTH__ is true ONLY for `vite --mode localdev` serve. A production build in that mode is refused, and
  // scripts/check-dist.mjs greps dist/ for LOCAL_DEV_AUTH_ACTIVE. (Not `local`: Vite reserves `.env.local` naming.)
  if (command === 'build' && mode === LOCAL_MODE) {
    throw new Error('The local-auth mode (localdev) cannot be used with `vite build`.');
  }
  const local = command === 'serve' && mode === LOCAL_MODE;
  return {
    plugins: [react(), ...(local ? [localDevPlugin()] : [])],
    define: {
      __E2E_MOCK_AUTH__: JSON.stringify(command === 'serve' && mode === 'e2e'),
      __LOCAL_AUTH__: JSON.stringify(local),
    },
    build: {
      target: 'es2023',
      rollupOptions: {
        input: { main: resolve(import.meta.dirname, 'index.html'), redirect: resolve(import.meta.dirname, 'redirect.html') },
      },
    },
    server: {
      port: LOCAL_PORT,
      ...(local && { host: 'localhost', strictPort: true }),
      proxy: {
        '/api': { target: BACKEND, changeOrigin: false, ws: false },
        ...(local && { '/local-auth': { target: BACKEND, changeOrigin: false, ws: false } }),
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
