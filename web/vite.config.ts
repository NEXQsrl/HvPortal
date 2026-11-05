import path from 'node:path';
import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';

// The SPA is served by the gateway (same origin, strict CSP, no inline scripts).
// Development: `npm run dev` rebuilds into the gateway wwwroot on change; open the gateway URL.
export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: { alias: { '@': path.resolve(__dirname, './src') } },
  build: {
    outDir: '../src/HvPortal.Gateway/wwwroot',
    emptyOutDir: true,
    sourcemap: false,
    assetsInlineLimit: 0, // never inline assets as data: URIs into scripts/styles
    chunkSizeWarningLimit: 1500,
  },
  test: { environment: 'node' },
});
