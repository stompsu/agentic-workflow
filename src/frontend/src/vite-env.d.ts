/// <reference types="vite/client" />

/** Compile-time flag, true only for `vite --mode e2e`. See vite.config.ts. */
declare const __E2E_MOCK_AUTH__: boolean;

/** Compile-time flag, true only for `vite --mode localdev` (serve). Local sign-in, ADR 0005. See vite.config.ts. */
declare const __LOCAL_AUTH__: boolean;
