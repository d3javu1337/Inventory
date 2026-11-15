import { defineConfig } from 'vitest/config';
export default defineConfig({ test: { environment: 'jsdom', setupFiles: './tests/setup.ts', css: true, exclude: ['tests/e2e/**', 'tests/live/**', 'tests/mobile-reports/**', 'node_modules/**'] } });
