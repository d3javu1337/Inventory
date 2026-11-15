import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './tests/live',
  outputDir: '../tmp/playwright-mobile-live',
  // Failure DOM snapshots may contain password input values, not just screenshots.
  preserveOutput: 'never',
  workers: 1,
  retries: 0,
  timeout: 60000,
  use: {
    actionTimeout: 15000,
    baseURL: 'http://127.0.0.1:5174',
    viewport: { width: 430, height: 900 },
    launchOptions: { executablePath: process.env.PLAYWRIGHT_CHROME_PATH || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe' },
    // No traces/videos: the real login and confirmation screens contain passwords.
    trace: 'off',
    video: 'off',
    screenshot: 'off',
  },
});
