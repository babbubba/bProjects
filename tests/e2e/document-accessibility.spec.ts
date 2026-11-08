import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { expect, test, type Locator, type Page } from '@playwright/test';
import { FakeParsing } from './support/fake-parsing';
import {
  FlowStack, completeDocumentAnalysis, completeDocumentDraft, documentSources, installFlowRoutes, type DocSource,
} from './support/fake-document-flow';
import { documentFixturesDir } from './support/documents';
import { checked, checkpoint, fieldContrast, focusAudit } from './support/a11y';

// T107 (E2E-OFFLINE, fake-stack nel browser, API simulate): accessibilita' del percorso documentale a 1440 e 390 px (progetti desktop-1440 /
// mobile-390) con controlli AUTOMATICI: axe-core (WCAG 2.0/2.1/2.2 A+AA, incluso il contrasto), scroll orizzontale della pagina, focus visibile,
// nomi accessibili, live region e ruoli degli errori. Non duplica accessibility.spec.ts (T051: shell, chat, anteprima, login) ne' i controlli
// funzionali da tastiera di document-small-flow/-parsing: aggiunge axe e overflow a OGNI fase del percorso documenti -> chat con fonti -> analisi
// con fonti documentali -> conferma -> bozza, gli stati d'errore/lavorazione dei documenti e il recupero dopo eventi SSE persi.
//
// LIMITI: i controlli automatici trovano solo i difetti che le regole sanno rilevare e NON attestano la conformita' WCAG; screen reader, zoom/reflow reali,
// ordine di lettura e qualita' degli annunci richiedono revisione umana (validation/accessibility.md). Il fake non e' revisione PM reale e non prova server,
// parser o persistenza. Nessun DGX, nessun PostgreSQL, nessuna variabile d'ambiente.

const P = 'p-1';

const fixture = (name: string) => readFileSync(join(documentFixturesDir, name));
const mediaTypes: Record<string, string> = {
  pdf: 'application/pdf', docx: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', md: 'text/markdown', txt: 'text/plain',
};
const asFile = (name: string) => ({ name, mimeType: mediaTypes[name.split('.').pop()!] ?? 'application/octet-stream', buffer: fixture(name) });

let fake: FlowStack;
let parsing: FakeParsing;
let sources: DocSource[];
let consoleErrors: string[];

test.beforeEach(async ({ page }) => {
  fake = new FlowStack();
  parsing = new FakeParsing();
  fake.parsing = parsing;
  sources = [];
  await fake.install(page);
  await installFlowRoutes(page, fake, () => sources);
  consoleErrors = [];
  page.on('pageerror', (e) => consoleErrors.push(`pageerror: ${e.message}`));
  page.on('console', (m) => {
    if (m.type() === 'error' && !m.text().startsWith('Failed to load resource')) consoleErrors.push(`console: ${m.text()}`);
  });
});

async function login(page: Page) {
  await page.goto('/');
  await expect(page).toHaveURL(/\/login$/);
  await page.getByLabel('Nome utente').fill('pm');
  await page.getByLabel('Password').fill('non-un-segreto');
  await page.getByRole('button', { name: 'Accedi' }).click();
  await expect(page.getByRole('heading', { name: 'Seleziona o crea un progetto' })).toBeVisible();
}

async function openProject(page: Page) {
  await login(page);
  await page.goto(`/projects/${P}`);
  await expect(page.getByRole('heading', { level: 1, name: 'Aurora' })).toBeVisible();
  await expect.poll(async () => (await fake.connections(page)).filter((c) => c.open && c.project === P).length).toBe(1);
}

const tab = async (page: Page, id: 'tab-work' | 'tab-documents') => {
  await page.getByTestId(id).focus();
  await page.keyboard.press('Enter');
};

/** Attiva un controllo con la tastiera (focus reale verificato + Invio). */
async function activate(page: Page, locator: Locator) {
  await expect(async () => {
    await locator.focus({ timeout: 1_000 });
    await expect(locator).toBeFocused({ timeout: 500 });
  }).toPass({ timeout: 8_000 });
  await page.keyboard.press('Enter');
}

const items = (page: Page) => page.getByTestId('docs-item');
const item = (page: Page, name: string) => items(page).filter({ hasText: name });

async function upload(page: Page, name: string) {
  await page.getByTestId('docs-file').setInputFiles(asFile(name));
  await activate(page, page.getByTestId('docs-upload'));
  await expect(item(page, name)).toHaveCount(1);
}

test.afterEach(async ({}, info) => {
  if (checked.length) console.log(`[T107 ${info.project.name}] ${info.title}\n  ` + checked.join('\n  '));
  checked.length = 0;
});

// ============================================================================================================== percorso completo

test.describe('[E2E fake] accessibilita del percorso documentale', () => {
  test('documenti, chat con fonti, analisi, conferma e bozza: axe, overflow, focus e nomi a ogni fase', async ({ page }) => {
    parsing.outcomes = ['Ready', 'Failed', 'Incomplete', 'Ready'];
    await openProject(page);
    await tab(page, 'tab-documents');
    await expect(page.getByTestId('docs-empty')).toBeVisible();
    await checkpoint(page, 'documenti: elenco vuoto');

    // --- Quattro formati con esiti diversi (Pronto, Non riuscito, Incompleto, Pronto): ogni stato e' un TESTO, il blocco e' dichiarato a parole.
    for (const name of ['md-basic.md', 'txt-conflict-a.txt', 'pdf-text.pdf', 'docx-basic.docx']) await upload(page, name);
    await expect(item(page, 'md-basic.md').getByTestId('docs-status')).toHaveText('Pronto');
    await expect(item(page, 'txt-conflict-a.txt').getByTestId('docs-status')).toHaveText('Preparazione non riuscita');
    await expect(item(page, 'pdf-text.pdf').getByTestId('docs-status')).toHaveText('Incompleto');
    await expect(page.getByTestId('docs-blocking')).toContainText('non ancora pronto');
    const texts = await page.getByTestId('docs-status').allInnerTexts();
    expect(new Set(texts.map((t) => t.trim())).size, 'stati distinguibili senza colore').toBe(3);
    await checkpoint(page, 'documenti: stati Pronto, Non riuscito, Incompleto e fonti bloccanti');
    await focusAudit(page, 'app-project-documents', 'elenco documenti');

    // --- Versioni aperte (aria-expanded/aria-controls), problema con azione di retry, passaggio letto.
    const failed = item(page, 'txt-conflict-a.txt');
    await activate(page, failed.getByTestId('docs-versions'));
    await expect(failed.getByTestId('docs-versions')).toHaveAttribute('aria-expanded', 'true');
    await expect(failed.locator(`#${await failed.getByTestId('docs-versions').getAttribute('aria-controls')}`)).toBeVisible();
    await expect(failed.getByTestId('docs-retry')).toBeVisible();
    await expect(failed.getByTestId('docs-issue')).toContainText(/.+/);
    await checkpoint(page, 'documenti: versioni con problema e azione di retry');
    const md = item(page, 'md-basic.md');
    await activate(page, md.getByTestId('docs-versions'));
    await activate(page, md.getByTestId('docs-read').first());
    await expect(md.getByTestId('passage-block').first()).toBeVisible();
    await expect(md.getByTestId('docs-read').first()).toHaveAttribute('aria-expanded', 'true');
    await checkpoint(page, 'documenti: contenuto letto da una versione');
    await focusAudit(page, 'app-project-documents', 'versioni e passaggio');

    // --- Errore di upload: alert con testo, focus spostato sull'errore, nessun colore come unico segnale.
    fake.fail('uploadDocument', 415);
    await page.getByTestId('docs-file').setInputFiles({ name: 'sconosciuto.exe', mimeType: 'application/octet-stream', buffer: Buffer.from('MZ') });
    await activate(page, page.getByTestId('docs-upload'));
    const uploadError = page.getByTestId('docs-upload-error');
    await expect(uploadError).toHaveAttribute('role', 'alert');
    await expect(uploadError).toContainText('Formato non supportato');
    await expect(uploadError).toBeFocused();
    await checkpoint(page, 'documenti: errore di caricamento (415)');

    // --- Retry da tastiera: l'esito e' annunciato nella live region (avviso con testo e focus), il documento diventa Pronto.
    await activate(page, failed.getByTestId('docs-versions'));   // una sola scheda versioni aperta alla volta
    await activate(page, failed.getByTestId('docs-retry'));
    await expect(failed.getByTestId('docs-status')).toHaveText('Pronto');
    await expect(page.getByTestId('docs-live').getByTestId('docs-notice')).toContainText('Nuovo tentativo');
    await expect(page.getByTestId('docs-notice')).toBeFocused();
    const incomplete = item(page, 'pdf-text.pdf');
    await activate(page, incomplete.getByTestId('docs-versions'));
    await activate(page, incomplete.getByTestId('docs-retry'));
    await expect(incomplete.getByTestId('docs-status')).toHaveText('Pronto');
    // Il fake rende Pronto subito (nel sistema reale il retry e In coda e il polling rilegge anche la selezione): qui si rilegge con Aggiorna.
    await activate(page, page.getByTestId('docs-refresh'));
    await expect(page.getByTestId('docs-blocking')).toHaveCount(0);
    await checkpoint(page, 'documenti: tutti pronti dopo il retry');

    // --- Chat con fonti: risposta con marcatori, fonte aperta da tastiera.
    sources = documentSources(fake);
    expect(sources.length).toBeGreaterThanOrEqual(4);
    await tab(page, 'tab-work');
    const box = page.getByLabel("Messaggio per l'assistente");
    await box.fill('Cosa dicono i documenti sul portale?');
    await box.press('Control+Enter');
    await expect(page.locator('article[data-role="user"]')).toHaveCount(1);
    const turnId = fake.project().turns[0]['id'] as string;
    const done = fake.completeTurn(P, turnId, `Il portale è per i clienti interni [fonte:${sources[0].sourceId}], con esclusioni [fonte:${sources[1].sourceId}].`);
    await fake.send(page, 'chat.state', done.turn);
    const assistant = page.locator('article[data-role="assistant"]');
    const chips = assistant.getByTestId('chat-source');
    await expect(chips).toHaveCount(2);
    await activate(page, chips.nth(0));
    await expect(assistant.getByTestId('chat-source-open')).toBeVisible();
    // L'unico messaggio finale sta nel registro (role=log) e le fonti si aprono con aria-expanded.
    expect(await assistant.evaluate((el) => el.closest('[aria-live]:not([aria-live=off])')?.getAttribute('role') ?? null)).toBe('log');
    await checkpoint(page, 'chat: risposta con fonti documentali aperte');
    await focusAudit(page, 'app-chat', 'chat con fonti');

    // --- Analizza: stato in corso annunciato come status, spinner decorativo; poi anteprima con fonti documentali.
    await activate(page, page.getByTestId('analyze'));
    const runState = page.getByTestId('run-state');
    await expect(runState).toHaveAttribute('role', 'status');
    await expect(runState.locator('.spinner-border')).toHaveAttribute('aria-hidden', 'true');
    await checkpoint(page, 'analisi: in corso');
    await completeDocumentAnalysis(page, fake, fake.project().runs[0]['id'] as string, sources);
    await expect(page.getByTestId('preview')).toBeVisible();
    const first = page.locator('[data-section=requirements] [data-testid=item]').first();
    await activate(page, first.getByTestId('item-sources'));
    await expect(first.getByTestId('passage-open')).toBeVisible();
    await checkpoint(page, 'analisi: anteprima con passaggio documentale aperto');
    await fieldContrast(page, 'analisi: anteprima');
    await focusAudit(page, 'app-analysis-preview', 'anteprima con fonti documentali');

    // --- Conferma e Genera: una sola azione primaria per fase; bozza con marcatori che sono pulsanti.
    await activate(page, page.getByTestId('confirm'));
    await expect(page.getByTestId('confirmed-badge')).toBeVisible();
    await checkpoint(page, 'analisi: confermata, Genera disponibile');
    await activate(page, page.getByTestId('generate'));
    await expect(page.getByTestId('generation-state')).toHaveAttribute('role', 'status');
    await checkpoint(page, 'bozza: generazione in corso');
    await completeDocumentDraft(page, fake, fake.project().runs.find((r) => r['kind'] === 'Document')!['id'] as string);
    await expect(page.getByTestId('draft')).toBeVisible();
    const sourceChips = page.getByTestId('markdown').getByTestId('source-chip');
    await expect(sourceChips).toHaveCount(sources.length);
    expect(await sourceChips.first().evaluate((e) => e.tagName)).toBe('BUTTON');
    await activate(page, sourceChips.nth(0));
    await expect(page.getByTestId('focused-source')).toBeVisible();
    await checkpoint(page, 'bozza: Markdown con fonti e fonte evidenziata');
    await focusAudit(page, 'app-document-view', 'bozza con fonti');

    // --- Versioni: nuova versione della fonte dopo la conferma, avviso dichiarato a parole.
    fake.sourceChanges = true;
    await page.reload();
    await expect(page.getByRole('heading', { level: 1, name: 'Aurora' })).toBeVisible();
    await expect(page.getByTestId('source-changes')).toContainText('Le fonti documentali sono cambiate');
    await expect(page.getByTestId('source-changes')).toHaveAttribute('role', 'status');
    await checkpoint(page, 'analisi e bozza: avviso di fonti cambiate');
    expect(consoleErrors).toEqual([]);
  });

  test('errori senza solo colore: ogni alert ha testo, ruolo e un nome azione; esito incerto riconciliabile da tastiera', async ({ page }) => {
    fake.fail('uploadDocument', 'drop-after-save');
    await openProject(page);
    await tab(page, 'tab-documents');
    await page.getByTestId('docs-file').setInputFiles(asFile('md-basic.md'));
    await activate(page, page.getByTestId('docs-upload'));
    const err = page.getByTestId('docs-upload-error');
    await expect(err).toHaveAttribute('role', 'alert');
    await expect(err).toContainText('Server non raggiungibile');
    await expect(err).toContainText('senza duplicati');
    await expect(err).toBeFocused();
    await checkpoint(page, 'documenti: esito incerto del caricamento');

    // Tutti gli alert visibili hanno testo (le live region vuote sono segnaposto legittimi e sono escluse): nessuna icona-sola.
    const bare = await page.evaluate(() =>
      [...document.querySelectorAll<HTMLElement>('.alert, [role=alert]')]
        .filter((e) => e.offsetParent !== null && (e.innerText ?? '').trim().length === 0 && e.getAttribute('data-testid') !== 'docs-live')
        .map((e) => e.className),
    );
    expect(bare, 'messaggi visibili senza testo').toEqual([]);

    // Lo stesso file, di nuovo da tastiera: riconcilia (una sola versione), l'alert sparisce e l'esito e' annunciato.
    await activate(page, page.getByTestId('docs-upload'));
    await expect(item(page, 'md-basic.md')).toHaveCount(1);
    await expect(page.getByTestId('docs-upload-error')).toHaveCount(0);
    await expect(page.getByTestId('docs-live').getByTestId('docs-notice')).toBeVisible();
    expect(fake.project().documents).toHaveLength(1);
    await checkpoint(page, 'documenti: dopo la riconciliazione');
    expect(consoleErrors).toEqual([]);
  });

  test('analisi bloccata da una fonte non pronta: errore dichiarato con il nome del documento e focus raggiungibile', async ({ page }) => {
    await page.route(/\/api\/projects\/p-1\/analyses$/, async (route) => {
      if (route.request().method() !== 'POST') return route.fallback();
      return route.fulfill({
        status: 409, contentType: 'application/json',
        body: JSON.stringify({ status: 409, code: 'sources_not_ready', title: 'sources_not_ready', blockingSources: [{ displayName: 'pdf-text.pdf', version: 1, status: 'Failed' }] }),
      });
    });
    parsing.outcomes = ['Failed'];
    fake.addPm(P, 'Cosa prevede il capitolato?');
    await openProject(page);
    await tab(page, 'tab-documents');
    await upload(page, 'pdf-text.pdf');
    await tab(page, 'tab-work');
    await activate(page, page.getByTestId('analyze'));
    const error = page.getByTestId('command-error');
    await expect(error).toHaveAttribute('role', 'alert');
    await expect(error).toContainText('pdf-text.pdf v1 (Failed)');
    await checkpoint(page, 'analisi: fonte non pronta');
    await expect(page.getByTestId('analyze')).toBeEnabled();
  });
});

// ============================================================================================================== eventi persi

test.describe('[E2E fake] recupero dopo eventi persi', () => {
  test('SSE caduto: l\'analisi completata nel frattempo compare alla riconnessione, senza comandi, con la pagina accessibile', async ({ page }) => {
    fake.addPm(P, 'Voglio un portale clienti');
    await openProject(page);
    await activate(page, page.getByTestId('analyze'));
    await expect(page.getByTestId('run-state')).toHaveAttribute('role', 'status');
    const writes = () => fake.calls.filter((c) => c.method !== 'GET' && !c.path.startsWith('/api/auth/')).length;
    const before = writes();

    // La riconnessione e' rifiutata una volta: l'evento di completamento va perso e lo stato si riconcilia con i GET.
    await fake.refuse(page, 503);
    await fake.drop(page);
    fake.completeAnalysis(P, fake.project().runs[0]['id'] as string);
    await expect(page.getByTestId('preview')).toBeVisible({ timeout: 20_000 });
    await expect(page.getByTestId('run-state')).toHaveCount(0);
    await expect.poll(async () => (await fake.connections(page)).filter((c) => c.open).length).toBe(1);
    expect(writes(), 'la riconnessione non emette comandi').toBe(before);
    await checkpoint(page, 'analisi: ripristinata dopo evento perso');
    // Il focus non e' stato rubato al comando che il PM aveva usato e nessun alert e' rimasto aperto.
    await expect(page.locator('[role=alert]:visible')).toHaveCount(0);
    expect(consoleErrors).toEqual([]);
  });

  test('documento terminato mentre il flusso era caduto: il polling lo mostra, lo annuncia e non sposta il focus', async ({ page }) => {
    parsing.delayPolls = 2;
    await openProject(page);
    await tab(page, 'tab-documents');
    await upload(page, 'md-basic.md');
    const row = item(page, 'md-basic.md');
    await expect(row.getByTestId('docs-status')).toHaveText('In coda');
    await expect(page.getByTestId('docs-queued')).toBeVisible();
    await checkpoint(page, 'documenti: in coda');

    await fake.refuse(page, 503);
    await fake.drop(page);
    const toggle = row.getByTestId('docs-versions');
    await toggle.focus();
    await expect(row.getByTestId('docs-status')).toHaveText('Pronto', { timeout: 20_000 });
    const note = page.getByTestId('docs-live').getByTestId('docs-progress-notice');
    await expect(note).toContainText('md-basic.md');
    await expect(note).toContainText('pronto');
    await expect(toggle, 'l\'annuncio non ruba il focus').toBeFocused();
    await checkpoint(page, 'documenti: terminato durante il polling, annunciato');
    expect(fake.calls.filter((c) => c.method !== 'GET' && !c.path.startsWith('/api/auth/') && !/\/documents$/.test(c.path))).toEqual([]);
    expect(consoleErrors).toEqual([]);
  });

  test('aggiornamento manuale con documenti ancora in lavorazione: Aggiorna e azionabile da tastiera e mantiene il focus', async ({ page }) => {
    parsing.delayPolls = 1000;
    await openProject(page);
    await tab(page, 'tab-documents');
    await upload(page, 'md-basic.md');
    // Il limite dei giri del polling automatico non e raggiungibile in tempi di prova: si verifica l'aggiornamento manuale.
    const refresh = page.getByTestId('docs-refresh');
    await activate(page, refresh);
    await expect(item(page, 'md-basic.md').getByTestId('docs-status')).toHaveText('In coda');
    await expect(refresh).toBeFocused();
    await checkpoint(page, 'documenti: aggiornamento manuale');
  });
});
