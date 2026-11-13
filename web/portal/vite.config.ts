import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

const api = process.env.S2E_API ?? 'http://localhost:5080';

// In development the portal proxies API calls, so the browser only ever talks to one origin.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    // Anchored patterns: "/r/…" (survey links) must not swallow the portal's own "/rules" route.
    proxy: Object.fromEntries(['^/api/', '^/assets/', '^/r/', '^/l/', '^/icons/', '^/addin/'].map((p) => [p, { target: api, changeOrigin: false }])),
  },
  build: { chunkSizeWarningLimit: 4000 },
});
