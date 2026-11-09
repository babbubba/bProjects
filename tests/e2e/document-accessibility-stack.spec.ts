import { expect, test } from '@playwright/test';
import { stack } from './support/real-stack/client';
import { checked, checkpoint, fieldContrast, focusAudit } from './support/a11y';
import { createProject, diagnose, docItem, login, openDocumentsTab, uploadDocument, unique, waitReady } from './support/real-stack/ui';

// T107, livello [E2E stack reale + LLM finto] (playwright.real.config.ts): backend REALE + PostgreSQL REALE (database isolato) + parser REALE (Docling);
// sono finte soltanto le risposte del modello (support/fake-llm.ts). Solo desktop-1440 (la configurazione ha un unico progetto): il livello a 390 px
// e' nel fake-stack (document-accessibility.spec.ts). I due esiti NON si sommano. Nessun DGX.
//
// Cosa aggiunge al fake-stack: gli STATI VERI prodotti dal sistema (documenti Pronto/Fallito dal parser reale, risposta della chat con fonti risolte dal server,
// analisi e bozza con marcatori reali) controllati con axe-core WCAG 2.2 A+AA, overflow e audit del focus. Non e' revisione PM reale.

test.describe.configure({ mode: 'serial' });

test.beforeEach(async () => {
  // La prova precedente puo' lasciare scritture in coda (telemetria, SSE): un deadlock del TRUNCATE e' transitorio e si ripete.
  for (let attempt = 1; ; attempt++) {
    try {
      await stack.resetData();
      break;
    } catch (e) {
      if (attempt >= 4 || !/deadlock/i.test(String(e))) throw e;
      await new Promise((r) => setTimeout(r, 2_000));
    }
  }
  await stack.resetLlm();
});

test.afterEach(async ({}, info) => {
  if (checked.length) console.log(`[T107 stack-reale] ${info.title}\n  ` + checked.join('\n  '));
  checked.length = 0;
});

test('[stack reale + LLM finto] percorso documentale con stati veri: axe, overflow, focus e nomi a ogni fase', async ({ page }) => {
  await login(page);
  await createProject(page, `T107 a11y ${unique()}`);
  await openDocumentsTab(page);
  await checkpoint(page, 'documenti: elenco vuoto');

  // --- Esiti veri del parser reale: Markdown e TXT pronti, PDF corrotto non pronto (testo, blocco dichiarato, azione di retry).
  await uploadDocument(page, 'md-basic.md');
  await waitReady(page, 'md-basic.md');
  await uploadDocument(page, 'txt-negations.txt');
  await waitReady(page, 'txt-negations.txt');
  await uploadDocument(page, 'pdf-corrupt.pdf');
  const corrupt = docItem(page, 'pdf-corrupt.pdf');
  await expect.poll(async () => (await corrupt.getByTestId('docs-status').innerText()).trim(), { timeout: 90_000 }).not.toMatch(/^(In coda|Lettura in corso|Estrazione in corso)$/);
  expect((await corrupt.getByTestId('docs-status').innerText()).trim()).not.toBe('Pronto');
  await expect(page.getByTestId('docs-blocking')).toBeVisible();
  await checkpoint(page, 'documenti: Pronto e non pronto dal parser reale');
  await focusAudit(page, 'app-project-documents', 'elenco documenti');

  await corrupt.getByTestId('docs-versions').click();
  await expect(corrupt.getByTestId('docs-version').first()).toBeVisible();
  await checkpoint(page, 'documenti: versioni del PDF corrotto');
  // Il PDF corrotto non e' una fonte: lo si esclude dalle fonti (checkbox con label) cosi da poter analizzare.
  await corrupt.getByLabel('Includi nelle fonti').uncheck();
  await expect(page.getByTestId('docs-blocking')).toHaveCount(0, { timeout: 15_000 });
  const md = docItem(page, 'md-basic.md');
  await md.getByTestId('docs-versions').click();
  await md.getByTestId('docs-read').first().click();
  await expect(md.getByTestId('passage-block').first()).toBeVisible();
  await checkpoint(page, 'documenti: contenuto letto (blocchi del parser reale)');
  await focusAudit(page, 'app-project-documents', 'versioni e passaggio');

  // --- Chat con fonti (modello finto, validatori reali).
  await page.getByTestId('tab-work').click();
  const box = page.getByLabel("Messaggio per l'assistente");
  await box.fill('Che cosa prevede la consultazione degli ordini?');
  await box.press('Control+Enter');
  const assistant = page.locator('article[data-role="assistant"]');
  await expect(assistant).toHaveCount(1, { timeout: 90_000 });
  const chip = assistant.getByTestId('chat-source').first();
  await expect(chip).toBeVisible();
  await chip.focus();
  await page.keyboard.press('Enter');
  await expect(assistant.getByTestId('chat-source-open')).toBeVisible();
  await checkpoint(page, 'chat: risposta con fonte aperta');
  await focusAudit(page, 'app-chat', 'chat con fonti');

  // --- Analisi, anteprima con fonte documentale, conferma, bozza.
  await page.getByTestId('analyze').focus();
  await page.keyboard.press('Enter');
  await expect(page.getByTestId('run-state')).toBeVisible();
  await expect.poll(async () => (await page.getByTestId('preview').count()) + (await page.getByTestId('run-error').count()), { timeout: 120_000 }).toBeGreaterThan(0);
  if ((await page.getByTestId('run-error').count()) > 0) await diagnose('analisi');
  await expect(page.getByTestId('run-error'), 'analisi senza errori').toHaveCount(0);
  const first = page.locator('[data-testid=item]').first();
  await first.getByTestId('item-sources').focus();
  await page.keyboard.press('Enter');
  await expect(first.getByTestId('passage-open').first()).toBeVisible();
  await checkpoint(page, 'analisi: anteprima con passaggio documentale aperto');
  await fieldContrast(page, 'analisi: anteprima');
  await focusAudit(page, 'app-analysis-preview', 'anteprima con fonti documentali');

  await page.getByTestId('confirm').focus();
  await page.keyboard.press('Enter');
  await expect(page.getByTestId('confirmed-badge')).toBeVisible({ timeout: 30_000 });
  await page.getByTestId('generate').focus();
  await page.keyboard.press('Enter');
  await expect.poll(async () => (await page.getByTestId('draft').count()) + (await page.getByTestId('generation-error').count()), { timeout: 120_000 }).toBeGreaterThan(0);
  if ((await page.getByTestId('generation-error').count()) > 0) await diagnose('generazione');
  await expect(page.getByTestId('generation-error'), 'generazione senza errori').toHaveCount(0);
  const chips = page.getByTestId('markdown').getByTestId('source-chip');
  expect(await chips.count()).toBeGreaterThan(0);
  await chips.first().focus();
  await page.keyboard.press('Enter');
  await expect(page.getByTestId('focused-source')).toBeVisible();
  await checkpoint(page, 'bozza: Markdown con fonti reali');
  await focusAudit(page, 'app-document-view', 'bozza con fonti');
  expect((await stack.llm()).unexpected, 'nessuna richiesta fuori protocollo al modello finto').toEqual([]);
});

test('[stack reale + LLM finto] rete assente e ritorno con il flusso caduto: nessun comando automatico, nessun alert residuo, pagina accessibile', async ({ page, context }) => {
  await login(page);
  await createProject(page, `T107 recupero ${unique()}`);
  await openDocumentsTab(page);
  await uploadDocument(page, 'md-basic.md');
  await waitReady(page, 'md-basic.md');
  await page.getByTestId('tab-work').click();
  const box = page.getByLabel("Messaggio per l'assistente");
  await box.fill('Che cosa prevede la consultazione degli ordini?');
  await box.press('Control+Enter');
  await expect(page.locator('article[data-role="user"]')).toHaveCount(1);
  await expect(page.locator('article[data-role="assistant"]')).toHaveCount(1, { timeout: 90_000 });

  // Offline: lo stream cade; online: il client riapre il flusso e riallinea lo stato senza comandi automatici.
  const commands: string[] = [];
  page.on('request', (r) => {
    if (r.method() !== 'GET' && /\/api\/projects\//.test(r.url())) commands.push(`${r.method()} ${new URL(r.url()).pathname}`);
  });
  await context.setOffline(true);
  await page.waitForTimeout(1_500);
  await context.setOffline(false);
  await expect.poll(async () => (await page.getByTestId('analyze').isEnabled()), { timeout: 30_000 }).toBe(true);
  await page.waitForTimeout(3_000);
  expect(commands, 'la riconnessione non emette comandi').toEqual([]);
  await expect(page.locator('[role=alert]:visible')).toHaveCount(0);
  await expect(page.locator('article[data-role="assistant"]')).toHaveCount(1);
  await checkpoint(page, 'chat: dopo offline/online');
});
