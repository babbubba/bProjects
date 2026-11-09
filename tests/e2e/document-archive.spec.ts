import { execSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { randomBytes } from 'node:crypto';
import { expect, test, type Locator, type Page } from '@playwright/test';
import { FakeStack } from './support/fake-stack';
import { sha256 } from './support/documents';

// Incremento I1 della slice 002 (T019): archivio documenti usato dal PM nel browser. DUE livelli distinti, da registrare separatamente:
//
//  [E2E fake]   API simulate nel browser (support/fake-stack.ts): nessun backend, PostgreSQL, volume, DGX o parser. Prova la UI e i
//               guasti deterministici (413/415/507, esito incerto con la stessa Idempotency-Key). Non prova il server.
//  [E2E reale]  Stack reale Angular+nginx+API+PostgreSQL+volume documenti, con LLM irraggiungibile e senza parser (nessun DGX).
//               Richiede E2E_BASE_URL, E2E_USER, E2E_PASSWORD (account PM da bootstrap-accounts), E2E_ADMIN_USER/E2E_ADMIN_PASSWORD
//               (facoltativi: controllo ruolo) ed E2E_RESTART_COMMAND (down senza -v + up --wait: senza, la prova di persistenza e'
//               ignorata, mai superata).
//
// Esecuzione per livello: `--grep "\[E2E fake\]"` oppure `--grep "\[E2E reale\]"`. Senza le variabili il livello reale risulta ignorato.

const screens = 'test-results/screens';
const unique = () => `${Date.now().toString(36)}${randomBytes(2).toString('hex')}`;

/** Contenuto binario non testuale e deterministico per la prova: l'eventuale alterazione dei byte e' rilevabile dall'hash. */
const payload = (label: string, size = 4096): Buffer => {
  const head = Buffer.from(`%PDF-1.4\n% ${label}\n`);
  const body = Buffer.alloc(size);
  for (let i = 0; i < size; i++) body[i] = (i * 31 + label.length * 7 + (i >> 3)) & 0xff;
  return Buffer.concat([head, body, Buffer.from([0x00, 0xff, 0x0d, 0x0a, 0x80])]);
};

const file = (name: string, buffer: Buffer, mimeType = 'application/pdf') => ({ name, mimeType, buffer });

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

async function upload(page: Page, f: { name: string; mimeType: string; buffer: Buffer }, displayName?: string) {
  await page.getByTestId('docs-file').setInputFiles(f);
  if (displayName !== undefined) await page.getByLabel('Nome del documento (facoltativo)').fill(displayName);
  await page.getByTestId('docs-upload').click();
}

async function showVersions(row: Locator) {
  const toggle = row.getByTestId('docs-versions');
  if ((await toggle.getAttribute('aria-expanded')) !== 'true') await toggle.click();
  await expect(row.getByTestId('docs-version').first()).toBeVisible();
}

async function uploadVersion(row: Locator, f: { name: string; mimeType: string; buffer: Buffer }) {
  await showVersions(row);
  await row.getByTestId('docs-newversion').setInputFiles(f);
}

/** Scarica la versione `index` (0 = prima in elenco) e restituisce nome e byte ricevuti dal browser. */
async function download(page: Page, row: Locator, index: number) {
  await showVersions(row);
  const [dl] = await Promise.all([
    page.waitForEvent('download'),
    row.getByTestId('docs-version').nth(index).getByTestId('docs-download').click(),
  ]);
  const path = await dl.path();
  return { name: dl.suggestedFilename(), bytes: readFileSync(path) };
}

function trackConsole(page: Page) {
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(`pageerror: ${e.message}`));
  page.on('console', (m) => {
    // I «Failed to load resource» sono gli HTTP 4xx attesi (refresh prima del login, bozza assente, errori provocati): quelli
    // inattesi si vedono come risposte >= 500 qui sotto o come asserzioni sulla UI.
    if (m.type() === 'error' && !m.text().startsWith('Failed to load resource')) errors.push(`console: ${m.text()}`);
  });
  page.on('response', (r) => {
    if (r.status() >= 500 && r.status() !== 507 && r.url().includes('/api/')) errors.push(`HTTP ${r.status()} ${new URL(r.url()).pathname}`);
  });
  return errors;
}

// ============================================================================================================== livello fake

test.describe('[E2E fake] archivio documenti con API simulate', () => {
  let fake: FakeStack;
  let consoleErrors: string[];

  test.beforeEach(async ({ page }) => {
    fake = new FakeStack();
    await fake.install(page);
    consoleErrors = trackConsole(page);
    await login(page, 'pm', 'non-un-segreto');
    await page.goto('/projects/p-1');
    await expect(page.getByRole('heading', { level: 1, name: 'Aurora' })).toBeVisible();
    await openDocuments(page);
  });

  test('carica, aggiunge v2, esclude, riapre e scarica byte identici; Queued leggibile, nessuna chiamata AI', async ({ page }, info) => {
    const v1 = payload('v1');
    const v2 = payload('v2', 6000);
    await expect(page.getByTestId('docs-empty')).toBeVisible();

    await upload(page, file('capitolato.pdf', v1));
    const row = item(page, 'capitolato.pdf');
    await expect(row).toHaveCount(1);
    await expect(row.getByTestId('docs-status')).toHaveText('In coda');
    await expect(row.getByTestId('docs-queued')).toContainText("l'originale è già archiviato e scaricabile");
    await expect(page.getByTestId('docs-blocking')).toContainText('non ancora pronto');

    await uploadVersion(row, file('capitolato-rev2.pdf', v2));
    await expect(row).toContainText('2 versioni');
    await expect(row.getByTestId('docs-version')).toHaveCount(2);

    // Esclusione persistente (PUT con If-Match): la preferenza sopravvive al ricaricamento della pagina.
    await row.getByTestId('docs-include').uncheck();
    await expect(row).toContainText('Esclusa dalle fonti');
    expect(fake.project('p-1').selection.revision).toBe(1);
    await page.screenshot({ path: `${screens}/I1-fake-${info.project.name}-queued-excluded.png`, fullPage: true });

    await page.reload();
    await openDocuments(page);
    const reopened = item(page, 'capitolato.pdf');
    await expect(reopened).toHaveCount(1);
    await expect(reopened.getByTestId('docs-include')).not.toBeChecked();
    await expect(reopened).toContainText('2 versioni');

    const first = await download(page, reopened, 0);
    const second = await download(page, reopened, 1);
    expect(first.name).toBe('capitolato.pdf');
    expect(sha256(first.bytes)).toBe(sha256(v1));
    expect(second.name).toBe('capitolato-rev2.pdf');
    expect(sha256(second.bytes)).toBe(sha256(v2));

    // L'archivio non invoca mai il modello: nessuna analisi, messaggio, documento o generazione.
    expect(fake.calls.filter((c) => /\/(analyses|messages|document-generations|chat-turns)/.test(c.path) && c.method !== 'GET')).toEqual([]);
    expect(consoleErrors).toEqual([]);
  });

  test('due file omonimi restano due documenti distinti con versioni proprie', async ({ page }) => {
    const a = payload('omonimo-a');
    const b = payload('omonimo-b', 5000);
    await upload(page, file('relazione.pdf', a));
    await expect(items(page)).toHaveCount(1);
    await upload(page, file('relazione.pdf', b));
    await expect(items(page)).toHaveCount(2);
    expect(fake.project('p-1').documents.map((d) => d.id)).toHaveLength(2);
    const second = items(page).nth(1);
    await expect(second).toContainText('1 versione');
    expect(sha256((await download(page, second, 0)).bytes)).toBe(sha256(b));
    expect(sha256((await download(page, items(page).nth(0), 0)).bytes)).toBe(sha256(a));
  });

  test('errori leggibili: formato, dimensione e quota; l\'archivio resta usabile e nulla viene creato', async ({ page }, info) => {
    fake.fail('uploadDocument', 415, 413);
    await upload(page, file('virus.exe', Buffer.from('MZ'), 'application/octet-stream'));
    await expect(page.getByTestId('docs-upload-error')).toContainText('Formato non supportato');
    await upload(page, file('enorme.pdf', payload('x')));
    await expect(page.getByTestId('docs-upload-error')).toContainText('supera la dimensione massima');

    fake.project('p-1').documentQuotaBytes = 3000;
    await upload(page, file('oltre-quota.pdf', payload('q', 4000)));
    const quota = page.getByTestId('docs-upload-error');
    await expect(quota).toContainText('quota dei documenti del progetto esauriti');
    await expect(quota).toHaveAttribute('role', 'alert');
    await page.screenshot({ path: `${screens}/I1-fake-${info.project.name}-quota-error.png`, fullPage: true });
    expect(fake.project('p-1').documents).toHaveLength(0);
    await expect(page.getByTestId('docs-empty')).toBeVisible();

    // Dopo gli errori un file nei limiti viene accettato.
    fake.project('p-1').documentQuotaBytes = null;
    await upload(page, file('piccolo.pdf', payload('ok', 100)));
    await expect(item(page, 'piccolo.pdf')).toHaveCount(1);
    expect(consoleErrors).toEqual([]);
  });

  test('esito incerto: ripetere lo stesso upload riconcilia la stessa ricevuta, mai un secondo documento', async ({ page }) => {
    fake.fail('uploadDocument', 'drop-after-save');
    const f = file('contratto.pdf', payload('contratto'));
    await upload(page, f);
    await expect(page.getByTestId('docs-upload-error')).toContainText('Server non raggiungibile');
    await expect(page.getByTestId('docs-upload-error')).toContainText('senza duplicati');

    // Il file resta selezionato nel form: Carica ripete lo stesso upload (stesso File, quindi stessa chiave).
    await page.getByTestId('docs-upload').click();
    await expect(item(page, 'contratto.pdf')).toHaveCount(1);
    const posts = fake.callsTo('POST', /\/documents$/);
    expect(posts).toHaveLength(2);
    expect(posts[0].key).toBeTruthy();
    expect(posts[1].key).toBe(posts[0].key);
    expect(fake.project('p-1').documents).toHaveLength(1);
    expect(fake.project('p-1').documents[0].versions).toHaveLength(1);
  });
});

// ============================================================================================================== livello reale

const baseUser = process.env['E2E_USER'];
const basePassword = process.env['E2E_PASSWORD'];
const adminUser = process.env['E2E_ADMIN_USER'];
const adminPassword = process.env['E2E_ADMIN_PASSWORD'];
const restartCommand = process.env['E2E_RESTART_COMMAND'];

test.describe('[E2E reale] archivio documenti su API + PostgreSQL + volume reali, LLM disabilitato', () => {
  test.skip(!baseUser || !basePassword || !process.env['E2E_BASE_URL'], 'E2E_BASE_URL/E2E_USER/E2E_PASSWORD non impostate: stack reale non disponibile.');
  test.describe.configure({ mode: 'serial' });

  const run = unique();
  const nameA = `Archivio A ${run}`;
  const nameB = `Archivio B ${run}`;
  const v1 = payload(`real-v1-${run}`, 20_000);
  const v2 = payload(`real-v2-${run}`, 33_000);
  const twinA = payload(`real-twin-a-${run}`, 7_000);
  const twinB = payload(`real-twin-b-${run}`, 8_000);
  const twinName = `omonimo-${run}.pdf`;
  let projectAUrl = '';
  let projectBUrl = '';
  let consoleErrors: string[] = [];

  test.beforeEach(({ page }) => {
    consoleErrors = trackConsole(page);
  });

  test('1. PM carica, v2 e due omonimi nel progetto A; Queued, esclusione e download byte-identico', async ({ page }, info) => {
    await login(page, baseUser!, basePassword!);
    await createProject(page, nameA);
    projectAUrl = page.url();
    await openDocuments(page);

    await upload(page, file(`piano-${run}.pdf`, v1));
    const row = item(page, `piano-${run}.pdf`);
    await expect(row).toHaveCount(1);
    await expect(row.getByTestId('docs-status')).toHaveText('In coda');
    await expect(row.getByTestId('docs-queued')).toBeVisible();

    await uploadVersion(row, file(`piano-${run}-rev2.pdf`, v2));
    await expect(row).toContainText('2 versioni');

    // Omonimi: stesso nome visibile, identita' distinte.
    await upload(page, file(twinName, twinA));
    await expect(item(page, twinName)).toHaveCount(1);
    await upload(page, file(twinName, twinB));
    await expect(item(page, twinName)).toHaveCount(2);

    const d1 = await download(page, row, 0);
    const d2 = await download(page, row, 1);
    expect(sha256(d1.bytes)).toBe(sha256(v1));
    expect(d1.bytes.equals(v1)).toBe(true);
    expect(sha256(d2.bytes)).toBe(sha256(v2));
    expect(d2.name).toBe(`piano-${run}-rev2.pdf`);
    expect(sha256((await download(page, item(page, twinName).nth(0), 0)).bytes)).toBe(sha256(twinA));
    expect(sha256((await download(page, item(page, twinName).nth(1), 0)).bytes)).toBe(sha256(twinB));

    await row.getByTestId('docs-include').uncheck();
    await expect(row).toContainText('Esclusa dalle fonti');
    await page.screenshot({ path: `${screens}/I1-reale-${info.project.name}-progetto-A.png`, fullPage: true });
    expect(consoleErrors).toEqual([]);
  });

  test('2. isolamento: il progetto B non vede i documenti di A e gli ID di A rispondono 404 da B', async ({ page, request }) => {
    await login(page, baseUser!, basePassword!);
    await createProject(page, nameB);
    projectBUrl = page.url();
    await openDocuments(page);
    await expect(page.getByTestId('docs-empty')).toBeVisible();
    await expect(items(page)).toHaveCount(0);

    // API diretta con un token PM: l'elenco di B e' vuoto e un documento di A cercato sotto B non esiste.
    const login1 = await request.post('/api/auth/login', { data: { userName: baseUser, password: basePassword } });
    expect(login1.ok()).toBe(true);
    const { accessToken } = (await login1.json()) as { accessToken: string };
    const auth = { Authorization: `Bearer ${accessToken}` };
    const idA = /\/projects\/([0-9a-f-]{36})/.exec(projectAUrl)![1];
    const idB = /\/projects\/([0-9a-f-]{36})/.exec(projectBUrl)![1];
    const listA = (await (await request.get(`/api/projects/${idA}/documents`, { headers: auth })).json()) as { items: { documentId: string }[] };
    expect(listA.items).toHaveLength(3);
    const listB = (await (await request.get(`/api/projects/${idB}/documents`, { headers: auth })).json()) as { items: unknown[] };
    expect(listB.items).toHaveLength(0);
    const cross = await request.get(`/api/projects/${idB}/documents/${listA.items[0].documentId}`, { headers: auth });
    expect(cross.status()).toBe(404);
    const crossDownload = await request.get(`/api/projects/${idB}/documents/${listA.items[0].documentId}/versions/1/download`, { headers: auth });
    expect(crossDownload.status()).toBe(404);
    expect(consoleErrors).toEqual([]);
  });

  test('3. ruoli: senza token 401, Admin senza ruolo PM 403 sull\'archivio', async ({ request }) => {
    const idA = /\/projects\/([0-9a-f-]{36})/.exec(projectAUrl)![1];
    expect((await request.get(`/api/projects/${idA}/documents`)).status()).toBe(401);
    test.skip(!adminUser || !adminPassword, 'E2E_ADMIN_USER/E2E_ADMIN_PASSWORD non impostate: controllo del ruolo Admin non eseguito.');
    const res = await request.post('/api/auth/login', { data: { userName: adminUser, password: adminPassword } });
    expect(res.ok()).toBe(true);
    const { accessToken } = (await res.json()) as { accessToken: string };
    const denied = await request.get(`/api/projects/${idA}/documents`, { headers: { Authorization: `Bearer ${accessToken}` } });
    expect(denied.status()).toBe(403);
  });

  test('4. errori reali: file non supportato (415) leggibile, archivio ancora usabile', async ({ page }, info) => {
    await login(page, baseUser!, basePassword!);
    await page.goto(projectAUrl);
    await openDocuments(page);
    const before = await items(page).count();
    await upload(page, file(`macro-${run}.xlsm`, Buffer.from('PK\x03\x04fake'), 'application/vnd.ms-excel.sheet.macroEnabled.12'));
    const err = page.getByTestId('docs-upload-error');
    await expect(err).toBeVisible();
    await expect(err).toHaveAttribute('role', 'alert');
    await expect(err).not.toBeEmpty();
    await page.screenshot({ path: `${screens}/I1-reale-${info.project.name}-errore-415.png`, fullPage: true });
    await expect(items(page)).toHaveCount(before);
  });

  test('5. continuita\' dopo RIAVVIO dello stack (down senza -v, up): documenti, versioni, omonimi, esclusione e byte invariati', async ({ page }, info) => {
    test.skip(!restartCommand, 'E2E_RESTART_COMMAND non impostata: il riavvio dello stack e\' parte essenziale della prova.');
    test.setTimeout(300_000);
    execSync(restartCommand!, { stdio: 'inherit', timeout: 240_000 });

    await login(page, baseUser!, basePassword!);
    await page.goto(projectAUrl);
    await expect(page.getByRole('heading', { level: 1, name: nameA })).toBeVisible();
    await openDocuments(page);
    await expect(items(page)).toHaveCount(3);
    const row = item(page, `piano-${run}.pdf`);
    await expect(row).toContainText('2 versioni');
    await expect(row.getByTestId('docs-include')).not.toBeChecked();
    await expect(row).toContainText('Esclusa dalle fonti');
    expect(sha256((await download(page, row, 0)).bytes)).toBe(sha256(v1));
    expect(sha256((await download(page, row, 1)).bytes)).toBe(sha256(v2));
    await expect(item(page, twinName)).toHaveCount(2);
    expect(sha256((await download(page, item(page, twinName).nth(0), 0)).bytes)).toBe(sha256(twinA));
    expect(sha256((await download(page, item(page, twinName).nth(1), 0)).bytes)).toBe(sha256(twinB));
    await page.screenshot({ path: `${screens}/I1-reale-${info.project.name}-dopo-riavvio.png`, fullPage: true });

    // B resta vuoto dopo il riavvio.
    await page.goto(projectBUrl);
    await openDocuments(page);
    await expect(items(page)).toHaveCount(0);
    expect(consoleErrors).toEqual([]);
  });
});
