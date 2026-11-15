import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  server: { port: 5173, proxy: { '/api': { target: 'http://localhost:5147', changeOrigin: true } } },
  build: { assetsDir: 'static', rollupOptions: { output: { manualChunks: { react: ['react','react-dom','react-router-dom'], mui: ['@mui/material','@mui/icons-material','@emotion/react','@emotion/styled'], query: ['@tanstack/react-query'] } } } },
});
