import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwind from '@tailwindcss/vite';
export default defineConfig({
  plugins: [react(), tailwind()],
  base: process.env.YARD_BASE_PATH ?? '/',
  server: {
    host: '127.0.0.1',
    port: Number(process.env.PORT ?? 5173),
    strictPort: true,
    watch: { usePolling: true, interval: 250 },
    hmr: { clientPort: Number(process.env.YARD_GATEWAY_PORT ?? 8080) },
  },
});
