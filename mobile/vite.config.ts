import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import path from 'node:path';

export default defineConfig({
  plugins: [react()],
  define: {
    __DEV__: JSON.stringify(true),
    global: 'globalThis',
    'process.env.ASSET_DEBUG_API_URL': JSON.stringify(''),
    'process.env.ASSET_RELEASE_API_URL': JSON.stringify(''),
  },
  resolve: {
    alias: [
      { find: 'react-native-camera-kit', replacement: path.resolve(__dirname, 'src/web/cameraKit.tsx') },
      { find: 'react-native-keychain', replacement: path.resolve(__dirname, 'src/web/keychain.ts') },
      { find: 'react-native', replacement: 'react-native-web' },
    ],
    extensions: ['.web.tsx', '.web.ts', '.web.jsx', '.web.js', '.tsx', '.ts', '.jsx', '.js', '.json'],
  },
  server: {
    host: '127.0.0.1',
    port: 5174,
    strictPort: true,
    proxy: {
      '/api': { target: process.env.ASSET_WEB_API_TARGET || 'http://127.0.0.1:5147', changeOrigin: true },
    },
  },
  optimizeDeps: {
    include: ['react-is', 'use-latest-callback', 'warn-once'],
    exclude: [
      'react-native-gesture-handler',
      'react-native-safe-area-context',
      'react-native-screens',
    ],
  },
  preview: { host: '127.0.0.1', port: 5174, strictPort: true },
  build: { outDir: 'dist-web', emptyOutDir: true },
});
