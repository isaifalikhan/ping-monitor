import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// In development the UI runs on Vite (http://localhost:5173) and proxies API/SignalR calls to the
// NetWatch service (https://localhost:8443, self-signed certificate). In production the built files are
// embedded into the service executable.
const backend = process.env.NETWATCH_BACKEND ?? 'https://localhost:8443';

export default defineConfig({
  plugins: [react()],
  base: '/',
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    sourcemap: false,
    chunkSizeWarningLimit: 1500,
    rollupOptions: {
      output: {
        manualChunks: {
          echarts: ['echarts/core', 'echarts/charts', 'echarts/components', 'echarts/renderers'],
          vendor: ['react', 'react-dom', 'react-router-dom', '@tanstack/react-query', '@microsoft/signalr'],
        },
      },
    },
  },
  server: {
    port: 5173,
    proxy: {
      '/api': { target: backend, secure: false, changeOrigin: false },
      '/hubs': { target: backend, secure: false, ws: true, changeOrigin: false },
    },
  },
  test: {
    environment: 'node',
  },
});
