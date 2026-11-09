import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { randomBytes } from 'node:crypto';
import { expect, test, type Locator, type Page } from '@playwright/test';
import { FakeStack } from './support/fake-stack';
import { FakeParsing } from './support/fake-parsing';
import { documentFixturesDir, sha256 } from './support/documents';

// Incremento I2 della slice 002 (T032, con T030/T117): consultazione del testo preparato, errore/incompletezza e retry manuale del
// parsing, usati dal PM nel browser. DUE livelli distinti, da registrare separatamente:
//
//  [E2E fake]   API simulate nel browser (support/fake-stack.ts + fake-parsing.ts): prova la UI e il contratto HTTP del parsing
//               (allowedActions, replay con la stessa Idempotency-Key, 409). Non prova il server né il parser.
//  [E2E reale]  Stack Docker reale (nginx + API + PostgreSQL + volume + parser Docling offline, profilo `parser`), LLM irraggiungibile:
//               nessun DGX. Richiede E2E_BASE_URL, E2E_USER, E2E_PASSWORD (account PM da bootstrap-accounts). Senza, risulta ignorato.
//
// Esecuzione per livello: `--grep "\[E2E fake\]"` oppure `--grep "\[E2E reale\]"`.

const screens = 'test-results/screens';
const unique = () => `${Date.now().toString(36)}${randomBytes(2).toString('hex')}`;
const fixture = (name: string) => readFileSync(join(documentFixturesDir, name));
const mediaTypes: Record<string, string> = {
  pdf: 'application/pdf',
  docx: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
  md: 'text/markdown',
  txt: 'text/plain',
};
const asFile = (name: string, buffer: Buffer = fixture(name)) => ({
  name,
  mimeType: mediaTypes[name.split('.').pop()!] ?? 'application/octet-stream',
  buffer,
});

async function openSidebar(page: Page) {
  if (page.viewportSize()!.width >= 992) return;
  const toggle = page.locator('.shell-toggle');
  await expect(toggle).toBeVisible();
  if ((await toggle.getAttribute('aria-expanded')) === 'false') await toggle.click();
}

async function login(page: Page, user: string, password: string) {
  await page.goto('/');
  await expect(page).toHaveURL(/\/login$/);
  await page.getByLabel('Nome utente').fill(user);
  await page.getByLabel('Password').fill(password);
  await page.getByRole('button', { name: 'Accedi' }).click();
  await expect(page.getByRole('heading', { name: 'Seleziona o crea un progetto' })).toBeVisible();
}

async function createProject(page: Page, name: string) {
  const before = page.url();
  await openSidebar(page);
  await page.getByRole('button', { name: 'Nuovo progetto' }).click();
  await page.getByLabel('Nome', { exact: true }).fill(name);
  await page.getByRole('button', { name: 'Crea progetto' }).click();
  await expect(page).not.toHaveURL(before);
  await expect(page.getByRole('heading', { level: 1, name })).toBeVisible();
}

const openDocuments = async (page: Page) => {
  await page.getByTestId('tab-documents').click();
  await expect(page.getByRole('heading', { name: 'Documenti', exact: true })).toBeVisible();
};

const items = (page: Page) => page.getByTestId('docs-item');
const item = (page: Page, name: string) => items(page).filter({ hasText: name });

async function upload(page: Page, f: { name: string; mimeType: string; buffer: Buffer }) {
  await page.getByTestId('docs-file').setInputFiles(f);
  await page.getByTestId('docs-upload').click();
  await expect(item(page, f.name)).toHaveCount(1);
}

async function showVersions(row: Locator) {
  const toggle = row.getByTestId('docs-versions');
  if ((await toggle.getAttribute('aria-expanded')) !== 'true') await toggle.click();
  await expect(row.getByTestId('docs-version').first()).toBeVisible();
}

async function download(row: Locator, page: Page) {
  await showVersions(row);
  const [dl] = await Promise.all([page.waitForEvent('download'), row.getByTestId('docs-download').first().click()]);
  return readFileSync((await dl.path())!);
}

async function openContent(row: Locator) {
  await showVersions(row);
  const read = row.getByTestId('docs-read').first();
  if ((await read.getAttribute('aria-expanded')) !== 'true') await read.click();
  await expect(row.getByTestId('passage-status')).toBeVisible();
}

function trackConsole(page: Page) {
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(`pageerror: ${e.message}`));
  page.on('console', (m) => {
    if (m.type() === 'error' && !m.text().startsWith('Failed to load resource')) errors.push(`console: ${m.text()}`);
  });
  page.on('response', (r) => {
    if (r.status() >= 500 && r.url().includes('/api/')) errors.push(`HTTP ${r.status()} ${new URL(r.url()).pathname}`);
  });
  return errors;
}

/** Richieste di retry inviate dal browser (POST .../document-jobs/{j}/retry): il retry non deve mai partire da solo. */
function trackRetries(page: Page) {
  const sent: { url: string; key: string | null }[] = [];
  page.on('request', (r) => {
    if (r.method() === 'POST' && /\/document-jobs\/[^/]+\/retry$/.test(new URL(r.url()).pathname)) {
      sent.push({ url: r.url(), key: r.headers()['idempotency-key'] ?? null });
    }
  });
  return sent;
}

// ============================================================================================================== livello fake

test.describe('[E2E fake] consultazione e retry del parsing con API simulate', () => {
  let fake: FakeStack;
  let parsing: FakeParsing;
  let consoleErrors: string[];
  let retries: { url: string; key: string | null }[];

  test.beforeEach(async ({ page }) => {
    fake = new FakeStack();
    parsing = new FakeParsing();
    fake.parsing = parsing;
    await fake.install(page);
    consoleErrors = trackConsole(page);
    retries = trackRetries(page);
    await login(page, 'pm', 'non-un-segreto');
    await page.goto('/projects/p-1');
    await expect(page.getByRole('heading', { level: 1, name: 'Aurora' })).toBeVisible();
    await openDocuments(page);
  });

  test('i quattro formati: stato Pronto, locator disponibili e passaggio con originale scaricabile', async ({ page }, info) => {
    const files = ['pdf-text.pdf', 'docx-basic.docx', 'md-basic.md', 'txt-negations.txt'];
    for (const name of files) await upload(page, asFile(name));
    for (const name of files) await expect(item(page, name).getByTestId('docs-status')).toHaveText('Pronto');
    await expect(page.getByTestId('docs-blocking')).toHaveCount(0);

    // PDF: pagina e area; il blocco di pagina 2 è distinto da quelli di pagina 1.
    const pdf = item(page, 'pdf-text.pdf');
    await openContent(pdf);
    await expect(pdf.getByTestId('passage-status')).toContainText('Pronto');
    await expect(pdf.getByTestId('passage-notready')).toHaveCount(0);
    const pdfBlocks = pdf.getByTestId('passage-block');
    await expect(pdfBlocks).toHaveCount(4);
    await expect(pdfBlocks.nth(0)).toContainText('Pagina 1');
    await expect(pdfBlocks.nth(2)).toContainText('Pagina 2');
    await expect(pdfBlocks.nth(2)).toContainText('area');
    await pdfBlocks.nth(1).getByRole('button', { name: 'Mostra passaggio' }).click();
    await expect(pdf.getByTestId('passage-open')).toContainText('La penale giornaliera');
    await expect(pdf.getByTestId('passage-open')).toContainText('Pagina 1');
    await page.screenshot({ path: `${screens}/I2-fake-${info.project.name}-pdf-passaggio.png`, fullPage: true });
    expect(sha256(await download(pdf, page))).toBe(sha256(fixture('pdf-text.pdf')));

    // DOCX: sezione, nessuna pagina né righe inventate.
    const docx = item(page, 'docx-basic.docx');
    await openContent(docx);
    const docxFirst = docx.getByTestId('passage-block').nth(1);
    await expect(docxFirst).toContainText('Requisiti › Esportazione');
    await expect(docxFirst).not.toContainText('Pagina');
    await expect(docxFirst).not.toContainText('riga');

    // Markdown e TXT: righe, nessuna pagina.
    const md = item(page, 'md-basic.md');
    await openContent(md);
    await expect(md.getByTestId('passage-block').nth(2)).toContainText('righe 5-6');
    await expect(md.getByTestId('passage-block').nth(2)).not.toContainText('Pagina');
    const txt = item(page, 'txt-negations.txt');
    await openContent(txt);
    await expect(txt.getByTestId('passage-block').nth(1)).toContainText('riga 2');
    expect(sha256(await download(txt, page))).toBe(sha256(fixture('txt-negations.txt')));

    expect(retries).toEqual([]);
    expect(consoleErrors).toEqual([]);
  });

  test('errore: l’originale resta scaricabile, il retry nasce da allowedActions e crea un solo job collegato', async ({ page }, info) => {
    parsing.outcomes = ['Failed', 'Ready'];
    await upload(page, asFile('pdf-text.pdf'));
    const row = item(page, 'pdf-text.pdf');
    await expect(row.getByTestId('docs-status')).toHaveText('Preparazione non riuscita');
    await expect(page.getByTestId('docs-blocking')).toBeVisible();
    await showVersions(row);
    await expect(row.getByTestId('docs-issue')).toContainText('non è riuscita');
    expect(sha256(await download(row, page))).toBe(sha256(fixture('pdf-text.pdf')));
    // refresh e rilettura non ritentano mai
    await page.getByTestId('docs-refresh').click();
    await expect(row.getByTestId('docs-status')).toHaveText('Preparazione non riuscita');
    expect(retries).toEqual([]);

    // doppio click: una sola richiesta
    const retry = row.getByTestId('docs-retry');
    await expect(retry).toBeVisible();
    await retry.dblclick();
    await expect(row.getByTestId('docs-retry-link')).toContainText('collegato al precedente');
    await expect(page.getByTestId('docs-notice')).toContainText('generazione 2');
    await expect(row.getByTestId('docs-status')).toHaveText('Pronto');
    await page.screenshot({ path: `${screens}/I2-fake-${info.project.name}-retry-collegato.png`, fullPage: true });

    expect(retries).toHaveLength(1);
    expect(parsing.retryJobsCreated).toBe(1);
    expect(parsing.gens.get(fake.project('p-1').documents[0].versions[0].id)).toHaveLength(2);
    // La storia resta: la generazione 1 è ancora leggibile e fallita; la 2 punta alla 1.
    const [g1, g2] = parsing.gens.get(fake.project('p-1').documents[0].versions[0].id)!;
    expect(g1.status).toBe('Failed');
    expect(g2.retryOfId).toBe(g1.id);
    await expect(row.getByTestId('docs-retry')).toHaveCount(0);
    expect(consoleErrors).toEqual([]);
  });

  test('esito incerto del retry: lo stesso pulsante riusa la stessa Idempotency-Key e non crea un secondo job', async ({ page }) => {
    parsing.outcomes = ['Failed', 'Ready'];
    await upload(page, asFile('pdf-text.pdf'));
    const row = item(page, 'pdf-text.pdf');
    await showVersions(row);
    fake.fail('retryParse', 'drop-after-save');
    await row.getByTestId('docs-retry').click();
    const error = row.getByTestId('docs-retry-error');
    await expect(error).toContainText('incerto');
    await expect(error).toHaveAttribute('role', 'alert');
    await expect(error).toBeFocused();
    expect(parsing.retryJobsCreated).toBe(1); // il server aveva già salvato

    await row.getByTestId('docs-retry').click();
    await expect(row.getByTestId('docs-retry-link')).toBeVisible();
    expect(retries).toHaveLength(2);
    expect(retries[1].key).toBe(retries[0].key);
    expect(parsing.retryJobsCreated).toBe(1);
    expect(parsing.gens.get(fake.project('p-1').documents[0].versions[0].id)).toHaveLength(2);
  });

  test('incompletezza: Incompleto con segnalazione, non utilizzabile come fonte e bloccante; retry ammesso', async ({ page }) => {
    parsing.outcomes = ['Incomplete', 'Incomplete'];
    await upload(page, asFile('pdf-mixed.pdf'));
    const row = item(page, 'pdf-mixed.pdf');
    await expect(row.getByTestId('docs-status')).toHaveText('Incompleto');
    await expect(row).toContainText('Non disponibile come fonte');
    await expect(page.getByTestId('docs-blocking')).toContainText('non ancora pronto');
    // la selezione che alimenta Analizza elenca il documento come bloccante e mai come risolto
    const selection = fake.project('p-1');
    expect(selection.documents).toHaveLength(1);

    await openContent(row);
    await expect(row.getByTestId('passage-status')).toContainText('Incompleto');
    await expect(row.getByTestId('passage-notready')).toBeVisible();
    await expect(row.getByTestId('passage-issue')).toContainText('Immagine a pagina 2');
    await expect(row.getByTestId('docs-retry')).toBeVisible();

    // escluderlo sblocca l'analisi senza renderlo pronto
    await row.getByTestId('docs-include').uncheck();
    await expect(page.getByTestId('docs-blocking')).toHaveCount(0);
    await expect(row).toContainText('Esclusa dalle fonti');
    expect(retries).toEqual([]);
  });

  test('aggiornamento automatico: In coda diventa Pronto senza azioni del PM e senza retry', async ({ page }) => {
    parsing.delayPolls = 2; // la rilettura dopo l’upload ne consuma una: la seconda arriva dal refresh automatico
    await upload(page, asFile('md-basic.md'));
    const row = item(page, 'md-basic.md');
    await expect(row.getByTestId('docs-status')).toHaveText('In coda');
    await expect(row.getByTestId('docs-queued')).toBeVisible();
    await expect(row.getByTestId('docs-status')).toHaveText('Pronto', { timeout: 20_000 });
    await expect(page.getByTestId('docs-blocking')).toHaveCount(0);
    expect(retries).toEqual([]);
  });
});

// ============================================================================================================== livello reale

const baseUser = process.env['E2E_USER'];
const basePassword = process.env['E2E_PASSWORD'];

test.describe('[E2E reale] parsing su API + PostgreSQL + parser Docling reali, LLM disabilitato', () => {
  test.skip(!baseUser || !basePassword || !process.env['E2E_BASE_URL'], 'E2E_BASE_URL/E2E_USER/E2E_PASSWORD non impostate: stack reale non disponibile.');
  test.describe.configure({ mode: 'serial' });

  const run = unique();
  const names = ['pdf-text.pdf', 'docx-basic.docx', 'md-basic.md', 'txt-negations.txt'];
  let projectUrl = '';
  let consoleErrors: string[] = [];
  let retries: { url: string; key: string | null }[] = [];

  test.beforeEach(({ page }) => {
    consoleErrors = trackConsole(page);
    retries = trackRetries(page);
  });

  async function token(request: import('@playwright/test').APIRequestContext) {
    const res = await request.post('/api/auth/login', { data: { userName: baseUser, password: basePassword } });
    expect(res.ok()).toBe(true);
    return { Authorization: `Bearer ${((await res.json()) as { accessToken: string }).accessToken}` };
  }
  const projectId = () => /\/projects\/([0-9a-f-]{36})/.exec(projectUrl)![1];

  test('1. PM carica i 4 formati: parsing reale completato ma NON Pronti (estrazione non avviata), testo con i locator e originale', async ({ page }, info) => {
    test.setTimeout(420_000);
    await login(page, baseUser!, basePassword!);
    await createProject(page, `Parsing ${run} ${info.project.name}`);
    projectUrl = page.url();
    await openDocuments(page);
    for (const name of names) await upload(page, asFile(name));
    // Aggiornamento automatico: nessun click su Aggiorna. Il parser reale termina e la preparazione passa a «Estrazione in corso»:
    // Ready richiede l'estrazione delle evidenze (I3, con il modello), quindi qui NESSUN documento è Pronto e tutti bloccano Analizza.
    for (const name of names) {
      await expect(item(page, name).getByTestId('docs-status')).toHaveText('Estrazione in corso', { timeout: 180_000 });
      await expect(item(page, name)).toContainText('Non disponibile come fonte');
    }
    await expect(page.getByTestId('docs-blocking')).toContainText('4 documento/i');

    const pdf = item(page, 'pdf-text.pdf');
    await openContent(pdf);
    await expect(pdf.getByTestId('passage-status')).toContainText('Estrazione in corso');
    await expect(pdf.getByTestId('passage-status')).not.toContainText('Pronto');
    await expect(pdf.getByTestId('passage-notready')).toBeVisible();
    expect(await pdf.getByTestId('passage-block').count()).toBeGreaterThan(1);
    await expect(pdf.getByTestId('passage-block').first()).toContainText('Pagina');
    await expect(pdf.getByTestId('passage-block').last()).toContainText('Pagina 2');
    await pdf.getByTestId('passage-block').first().getByRole('button', { name: 'Mostra passaggio' }).click();
    await expect(pdf.getByTestId('passage-open')).toBeVisible();
    await page.screenshot({ path: `${screens}/I2-reale-${info.project.name}-pdf-passaggio.png`, fullPage: true });

    for (const name of ['docx-basic.docx', 'md-basic.md', 'txt-negations.txt']) {
      const row = item(page, name);
      await openContent(row);
      await expect(row.getByTestId('passage-status')).toContainText('Estrazione in corso');
      await expect(row.getByTestId('passage-notready')).toBeVisible();
      const blocks = row.getByTestId('passage-block');
      expect(await blocks.count()).toBeGreaterThan(0);
      const text = (await blocks.allTextContents()).join('\n');
      expect(text).not.toContain('Pagina'); // nessuna pagina simulata per formati senza paginazione
      await blocks.first().getByRole('button', { name: 'Mostra passaggio' }).click();
      await expect(row.getByTestId('passage-open')).toBeVisible();
    }
    for (const name of names) {
      expect(sha256(await download(item(page, name), page))).toBe(sha256(fixture(name)));
    }
    expect(retries).toEqual([]); // nessun retry da refresh/polling
    expect(consoleErrors).toEqual([]);
  });

  test('2. incompleto e fallito: segnalazioni, originale scaricabile, retry manuale = UN solo nuovo job collegato', async ({ page, request }, info) => {
    test.setTimeout(420_000);
    await login(page, baseUser!, basePassword!);
    await page.goto(projectUrl);
    await openDocuments(page);
    await upload(page, asFile('pdf-mixed.pdf'));
    await upload(page, asFile('pdf-corrupt.pdf'));
    const mixed = item(page, 'pdf-mixed.pdf');
    const corrupt = item(page, 'pdf-corrupt.pdf');
    await expect(mixed.getByTestId('docs-status')).toHaveText('Incompleto', { timeout: 180_000 });
    await expect(corrupt.getByTestId('docs-status')).toHaveText('Preparazione non riuscita', { timeout: 180_000 });
    await expect(mixed).toContainText('Non disponibile come fonte');
    await expect(page.getByTestId('docs-blocking')).toContainText('non ancora pronto');
    expect(retries).toEqual([]); // l'attesa e il polling non hanno ritentato

    // Incompleto: contenuto letto ma non pronto, segnalazione visibile.
    await openContent(mixed);
    await expect(mixed.getByTestId('passage-notready')).toBeVisible();
    expect(await mixed.getByTestId('passage-issue').count()).toBeGreaterThan(0);
    // Fallito: nessun contenuto, originale scaricabile.
    await showVersions(corrupt);
    await expect(corrupt.getByTestId('docs-issue')).toBeVisible();
    expect(sha256(await download(corrupt, page))).toBe(sha256(fixture('pdf-corrupt.pdf')));
    expect(sha256(await download(mixed, page))).toBe(sha256(fixture('pdf-mixed.pdf')));
    await page.screenshot({ path: `${screens}/I2-reale-${info.project.name}-errore-incompleto.png`, fullPage: true });

    // Retry manuale sull'incompleto (doppio click: una sola richiesta).
    const auth = await token(request);
    const prepUrl = async (doc: string, n: number | string) => `/api/projects/${projectId()}/documents/${doc}/versions/1/preparations/${n}`;
    const list = (await (await request.get(`/api/projects/${projectId()}/documents`, { headers: auth })).json()) as { items: { documentId: string; displayName: string }[] };
    const docId = list.items.find((d) => d.displayName === 'pdf-mixed.pdf')!.documentId;
    const before = (await (await request.get(await prepUrl(docId, 'latest'), { headers: auth })).json()) as { id: string; generation: number; parseJobId: string; allowedActions: string[] };
    expect(before.generation).toBe(1);
    expect(before.allowedActions).toContain('retry');

    await mixed.getByTestId('docs-retry').dblclick();
    await expect(mixed.getByTestId('docs-retry-link')).toContainText('collegato al precedente');
    expect(retries).toHaveLength(1);
    await expect(page.getByTestId('docs-notice')).toContainText('generazione 2');

    const after = (await (await request.get(await prepUrl(docId, 'latest'), { headers: auth })).json()) as { id: string; generation: number; parseJobId: string; retryOfId: string };
    expect(after.generation).toBe(2);
    expect(after.retryOfId).toBe(before.id);
    expect(after.parseJobId).not.toBe(before.parseJobId);
    // La storia resta consultabile e nessuna terza generazione è nata.
    expect((await request.get(await prepUrl(docId, 1), { headers: auth })).status()).toBe(200);
    expect((await request.get(await prepUrl(docId, 3), { headers: auth })).status()).toBe(404);
    // Ripetere il retry sul vecchio job con una chiave nuova non crea un altro job: 409 con codice stabile.
    const again = await request.post(`/api/projects/${projectId()}/document-jobs/${before.parseJobId}/retry`, {
      headers: { ...auth, 'Idempotency-Key': crypto.randomUUID() },
    });
    expect(again.status()).toBe(409);
    expect(['job_superseded', 'job_active', 'preparation_active']).toContain(((await again.json()) as { code: string }).code);
    expect((await request.get(await prepUrl(docId, 3), { headers: auth })).status()).toBe(404);

    // Lo stato persistito si vede anche dopo il ricaricamento della pagina; l'originale resta identico.
    await page.reload();
    await openDocuments(page);
    const reopened = item(page, 'pdf-mixed.pdf');
    await expect(reopened.getByTestId('docs-status')).toHaveText(/Incompleto|In coda|Lettura in corso/, { timeout: 120_000 });
    expect(sha256(await download(reopened, page))).toBe(sha256(fixture('pdf-mixed.pdf')));
    expect(consoleErrors).toEqual([]);
  });

  test('3. fonte non pronta non è utilizzabile da Analizza: 409 sources_not_ready, nessun run', async ({ request, page }) => {
    const auth = await token(request);
    const res = await request.post(`/api/projects/${projectId()}/analyses`, { headers: { ...auth, 'Idempotency-Key': crypto.randomUUID() } });
    expect(res.status()).toBe(409);
    const body = (await res.json()) as { code: string; blockingSources: { displayName: string }[] };
    expect(body.code).toBe('sources_not_ready');
    const blocked = body.blockingSources.map((b) => b.displayName);
    expect(blocked).toContain('pdf-mixed.pdf');
    expect(blocked).toContain('pdf-corrupt.pdf');
    for (const name of names) expect(blocked).toContain(name); // parsing terminato ma estrazione non avviata: nessuna fonte Ready
    const selection = (await (await request.get(`/api/projects/${projectId()}/source-selection`, { headers: auth })).json()) as {
      items: { availability: string }[];
    };
    expect(selection.items.filter((i) => i.availability === 'Ready')).toHaveLength(0);
    expect(selection.items.length).toBeGreaterThanOrEqual(6);

    await login(page, baseUser!, basePassword!);
    await page.goto(projectUrl);
    await openDocuments(page);
    await expect(page.getByTestId('docs-blocking')).toBeVisible();
  });
});
