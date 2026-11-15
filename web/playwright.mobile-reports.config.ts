import { defineConfig } from '@playwright/test';
export default defineConfig({
  testDir: './tests/mobile-reports',
  outputDir: '../tmp/playwright-mobile-reports',
  preserveOutput: 'never',
  workers: 1,
  use: {
    baseURL: 'http://127.0.0.1:5174', viewport: { width: 430, height: 900 },
    launchOptions: { executablePath: process.env.PLAYWRIGHT_CHROME_PATH || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe' },
    trace: 'off', screenshot: 'off', video: 'off',
  },
  webServer: { command: 'npm --prefix ../mobile run web', url: 'http://127.0.0.1:5174', reuseExistingServer: true },
});
