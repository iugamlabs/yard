import { defineConfig, devices } from '@playwright/test';
export default defineConfig({
  testDir: './browser',
  workers: 1,
  fullyParallel: false,
  retries: 0,
  timeout: 45000,
  reporter: [['list']],
  outputDir: '../.temp/browser-results',
  use: {
    baseURL: process.env.YARD_VERIFY_URL ?? 'http://localhost:8080',
    screenshot: 'only-on-failure',
    trace: 'retain-on-failure',
  },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'] } },
    { name: 'mobile', use: { ...devices['Pixel 5'] } },
  ],
});
