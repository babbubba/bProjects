import { defineConfig, devices } from '@playwright/test';

// Se E2E_BASE_URL è impostata i test usano uno stack già avviato (es. Docker Compose);
// altrimenti Playwright avvia il dev server Angular.
const baseURL = process.env['E2E_BASE_URL'];

// Livelli di prova della 002 (T003): senza E2E_BASE_URL le prove usano il fake-stack (E2E-OFFLINE, nessun DGX né PostgreSQL);
// con E2E_BASE_URL e uno stack reale con LLM finto si distingue l'esito. Le prove DGX/PM-DGX non girano da qui.
// Le fixture dei documenti sono in tests/fixtures/documents (vedi support/documents.ts).
export default defineConfig({
  testDir: '.',
  testMatch: '**/*.spec.ts',
  // Gli stack reali con modello finto (playwright.real.config.ts) non girano da qui.
  testIgnore: ['**/real-stack-*.spec.ts', '**/*-stack.spec.ts'],
  // Gli artefatti dei test stanno in una sottocartella: gli screenshot in test-results/screens non vengono cancellati a ogni esecuzione.
  outputDir: './test-results/artifacts',
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
