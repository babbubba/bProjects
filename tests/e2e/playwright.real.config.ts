import { defineConfig, devices } from '@playwright/test';

// Livello "stack reale con modello finto" (T092 / T103): backend REALE + PostgreSQL REALE (database isolato) + parser REALE; sono finte solo le risposte
// del modello (support/fake-llm.ts). globalSetup avvia/ferma tutto (anche su fallimento); nessun DGX. Esecuzione: `npx playwright test -c playwright.real.config.ts`.
export default defineConfig({
  testDir: '.',
  testMatch: ['**/real-stack-*.spec.ts', '**/*-stack.spec.ts', 'document-version-recovery.spec.ts'],
  outputDir: '../../diagnostics-local/real-stack/artifacts',
  globalSetup: './real-stack.global-setup.ts',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 180_000,
  reporter: [['list'], ['html', { open: 'never', outputFolder: '../../diagnostics-local/real-stack/report' }]],
  use: { baseURL: `http://127.0.0.1:${process.env['E2E_STACK_PROXY_PORT'] ?? 4290}`, trace: 'retain-on-failure' },
  projects: [{ name: 'desktop-1440', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 } } }],
});
