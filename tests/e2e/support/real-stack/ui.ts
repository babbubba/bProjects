import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { expect, type Locator, type Page } from '@playwright/test';
import { documentFixturesDir, sha256 } from '../documents';
import { stack } from './client';

// Operazioni di interfaccia condivise dalle prove sullo stack reale con modello finto (T092 e T103).

export const unique = () => `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`;
export const mediaTypes: Record<string, string> = {
  pdf: 'application/pdf', docx: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', md: 'text/markdown', txt: 'text/plain',
};
export const fixtureBytes = (name: string) => readFileSync(join(documentFixturesDir, name));
export const asFile = (name: string, bytes: Buffer = fixtureBytes(name)) => ({ name, mimeType: mediaTypes[name.split('.').pop()!] ?? 'application/octet-stream', buffer: bytes });
export { sha256 };

export async function login(page: Page) {
  await page.goto('/');
  await expect(page).toHaveURL(/\/login$/);
  await page.getByLabel('Nome utente').fill(stack.user());
  await page.getByLabel('Password').fill(stack.password());
  await page.getByRole('button', { name: 'Accedi' }).click();
  await expect(page.getByRole('heading', { name: 'Seleziona o crea un progetto' })).toBeVisible({ timeout: 20_000 });
}

/** Crea un progetto dalla UI e ne restituisce l'id (dall'URL). */
export async function createProject(page: Page, name: string): Promise<string> {
  const before = page.url();
  await page.getByRole('button', { name: 'Nuovo progetto' }).click();
  await page.getByLabel('Nome', { exact: true }).fill(name);
  await page.getByRole('button', { name: 'Crea progetto' }).click();
  await expect(page).not.toHaveURL(before);
  await expect(page.getByRole('heading', { level: 1, name })).toBeVisible();
  return /\/projects\/([0-9a-f-]{36})/.exec(page.url())![1];
}

export async function openProject(page: Page, projectId: string, name: string) {
  await page.goto(`/projects/${projectId}`);
  await expect(page.getByRole('heading', { level: 1, name })).toBeVisible({ timeout: 20_000 });
}

export async function openDocumentsTab(page: Page) {
  await page.getByTestId('tab-documents').click();
  await expect(page.getByRole('heading', { name: 'Documenti', exact: true })).toBeVisible();
}

export const docItem = (page: Page, name: string): Locator => page.getByTestId('docs-item').filter({ hasText: name });

export async function uploadDocument(page: Page, name: string, bytes?: Buffer) {
  await page.getByTestId('docs-file').setInputFiles(asFile(name, bytes));
  await page.getByTestId('docs-upload').click();
  await expect(docItem(page, name)).toHaveCount(1);
}

export async function waitReady(page: Page, name: string, timeout = 90_000) {
  await expect(docItem(page, name).getByTestId('docs-status')).toHaveText('Pronto', { timeout });
}

export async function showVersions(page: Page, name: string) {
  const item = docItem(page, name);
  if ((await item.getByTestId('docs-version').count()) === 0) await item.getByTestId('docs-versions').click();
  await expect(item.getByTestId('docs-version').first()).toBeVisible();
}

export async function uploadVersion(page: Page, name: string, bytes: Buffer) {
  await showVersions(page, name);
  await docItem(page, name).getByTestId('docs-newversion').setInputFiles(asFile(name, bytes));
}

/** Conteggio dei "lavori" persistiti: nessuna lettura, riconnessione o cambio pagina deve modificarlo. */
export async function workCounts(): Promise<Record<string, number>> {
  const [row] = await stack.sql<Record<string, number>>(`SELECT
    (SELECT count(*) FROM "DocumentJobs")::int AS jobs, (SELECT count(*) FROM "DocumentPreparations")::int AS preparations,
    (SELECT count(*) FROM "EvidenceExtractionJobs")::int AS "extractionJobs", (SELECT count(*) FROM "ProcessingRuns")::int AS runs,
    (SELECT count(*) FROM "ChatTurns")::int AS turns, (SELECT count(*) FROM "InferenceCalls")::int AS calls,
    (SELECT count(*) FROM "DocumentVersions")::int AS versions, (SELECT count(*) FROM "DocumentPromotions")::int AS promotions`);
  return row;
}

/** Diagnostica dei run/turni falliti e delle richieste inattese al modello finto: stampata prima di far fallire una prova. */
export async function diagnose(label: string) {
  const runs = await stack.sql(`SELECT "Kind"::text, "Status"::text, "ErrorCode", "ErrorMessage" FROM "ProcessingRuns" ORDER BY "RequestedAt" DESC LIMIT 5`);
  const llm = await stack.llm();
  console.log(`[diagnose ${label}] runs=${JSON.stringify(runs)} unexpected=${JSON.stringify(llm.unexpected)} graphs=${llm.requests.map((r) => `${r.graph}:${r.outcome}`).join(',')}`);
}
