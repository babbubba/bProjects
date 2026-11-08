import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { expect, test, type Page } from '@playwright/test';
import { documentFixturesDir, sha256 } from './support/documents';
import { stack } from './support/real-stack/client';

// SMOKE dell'harness "stack reale con modello finto" (playwright.real.config.ts): backend, PostgreSQL (database isolato) e parser REALI, risposte del
// modello simulate da support/fake-llm.ts. Una richiesta AI attraverso la vera API (estrazione dopo l'upload + un turno di chat), risposta
// deterministica valida e persistenza verificata in PostgreSQL. Nessun DGX.

const unique = () => `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`;

async function login(page: Page) {
  await page.goto('/');
  await expect(page).toHaveURL(/\/login$/);
  await page.getByLabel('Nome utente').fill(stack.user());
  await page.getByLabel('Password').fill(stack.password());
  await page.getByRole('button', { name: 'Accedi' }).click();
  await expect(page.getByRole('heading', { name: 'Seleziona o crea un progetto' })).toBeVisible({ timeout: 20_000 });
}

test('[stack reale + LLM finto] smoke: upload -> parser reale -> estrazione (modello finto) -> Pronto -> chat con fonte -> persistenza in PostgreSQL', async ({ page }) => {
  await stack.resetLlm();
  await login(page);
  const name = `Smoke ${unique()}`;
  await page.getByRole('button', { name: 'Nuovo progetto' }).click();
  await page.getByLabel('Nome', { exact: true }).fill(name);
  await page.getByRole('button', { name: 'Crea progetto' }).click();
  await expect(page.getByRole('heading', { level: 1, name })).toBeVisible();

  await page.getByTestId('tab-documents').click();
  const bytes = readFileSync(join(documentFixturesDir, 'md-basic.md'));
  await page.getByTestId('docs-file').setInputFiles({ name: 'md-basic.md', mimeType: 'text/markdown', buffer: bytes });
  await page.getByTestId('docs-upload').click();
  const item = page.getByTestId('docs-item').filter({ hasText: 'md-basic.md' });
  await expect(item.getByTestId('docs-status')).toHaveText('Pronto', { timeout: 90_000 });

  // Persistenza: originale con lo stesso hash, estrazione AI avvenuta solo per le unita documentali.
  const stored = await stack.sql<{ Sha256: string; OriginalName: string }>('SELECT "Sha256", "OriginalName" FROM "DocumentVersions"');
  expect(stored).toEqual([{ Sha256: sha256(bytes), OriginalName: 'md-basic.md' }]);
  const llm = await stack.llm();
  expect(llm.unexpected, 'nessuna richiesta inattesa al modello finto').toEqual([]);
  expect(llm.requests.filter((r) => r.graph === 'extract').length).toBeGreaterThan(0);
  expect(llm.requests.every((r) => r.graph === 'extract' || r.graph === 'extract-pm')).toBe(true);

  await page.getByTestId('tab-work').click();
  const box = page.getByLabel("Messaggio per l'assistente");
  await box.fill('Che cosa prevede il documento sui requisiti?');
  await box.press('Control+Enter');
  const assistant = page.locator('article[data-role="assistant"]');
  await expect(assistant).toHaveCount(1, { timeout: 90_000 });
  await expect(assistant.getByTestId('chat-source').first()).toBeVisible();
  expect(await page.getByTestId('source-chip-unknown').count()).toBe(0);

  const turns = await stack.sql<{ Status: string }>(`SELECT "Status" FROM "ChatTurns"`);
  expect(turns.map((t) => t.Status)).toEqual(['Completed']);
  const messages = await stack.sql<{ Role: string; Body: string }>(`SELECT "Role", "Body" FROM "ConversationMessages" ORDER BY "Sequence"`);
  expect(messages.map((m) => m.Role)).toEqual(['User', 'Assistant']);
  expect(messages[1].Body).toMatch(/\[fonte:[0-9a-f-]{36}\]/);
  console.log('[smoke] hash fixture', sha256(bytes));
  const after = await stack.llm();
  expect(after.unexpected).toEqual([]);
  console.log('[smoke] richieste al modello finto:', after.requests.map((r) => `${r.graph}:${r.outcome}`).join(' '));
});
