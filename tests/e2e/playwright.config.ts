import { defineConfig, devices } from '@playwright/test';

// Se E2E_BASE_URL è impostata i test usano uno stack già avviato (es. Docker Compose);
// altrimenti Playwright avvia il dev server Angular.
const baseURL = process.env['E2E_BASE_URL'];

export default defineConfig({
  testDir: '.',
  testMatch: '**/*.spec.ts',
  fullyParallel: false,
  workers: 1, // i test condividono lo stesso stack e uno di essi lo riavvia
  forbidOnly: !!process.env['CI'],
  retries: process.env['CI'] ? 1 : 0,
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL: baseURL ?? 'http://localhost:4200',
    trace: 'retain-on-failure',
  },
  projects: [
    { name: 'desktop-1440', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 } } },
    { name: 'mobile-390', use: { ...devices['Desktop Chrome'], viewport: { width: 390, height: 844 } } },
  ],
  webServer: baseURL
    ? undefined
    : {
        command: 'npm run start -- --port 4200',
        cwd: '../../frontend',
        url: 'http://localhost:4200',
        reuseExistingServer: !process.env['CI'],
        timeout: 120_000,
      },
});
