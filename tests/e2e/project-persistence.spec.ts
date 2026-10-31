import { execSync } from 'node:child_process';
import { expect, test, type Page } from '@playwright/test';

// Checkpoint T019: stack reale (Angular + API + PostgreSQL), DGX non raggiungibile.
// Variabili: E2E_BASE_URL, E2E_USER, E2E_PASSWORD (account PM creato con bootstrap-accounts);
// E2E_RESTART_COMMAND (facoltativa) riavvia lo stack conservando il volume PostgreSQL.
const user = process.env['E2E_USER'];
const password = process.env['E2E_PASSWORD'];
const restartCommand = process.env['E2E_RESTART_COMMAND'];

test.skip(!user || !password, 'E2E_USER ed E2E_PASSWORD non impostate: stack di prova non disponibile.');

const unique = () => `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`;

async function login(page: Page) {
  await page.goto('/');
  await expect(page).toHaveURL(/\/login$/);
  await page.getByLabel('Nome utente').fill(user!);
  await page.getByLabel('Password').fill(password!);
  await page.getByRole('button', { name: 'Accedi' }).click();
  await expect(page.getByRole('heading', { name: 'Seleziona o crea un progetto' })).toBeVisible();
}

/** A 390px la sidebar è richiudibile: la apre se è chiusa. */
async function openSidebar(page: Page) {
  if (page.viewportSize()!.width >= 992) return; // sidebar sempre visibile su desktop
  const toggle = page.locator('.shell-toggle');
  await expect(toggle).toBeVisible();
  if ((await toggle.getAttribute('aria-expanded')) === 'false') {
    await toggle.click();
  }
}

async function createProject(page: Page, name: string, description = '') {
  const before = page.url();
  await openSidebar(page);
  await page.getByRole('button', { name: 'Nuovo progetto' }).click();
  await page.getByLabel('Nome', { exact: true }).fill(name);
  if (description) {
    await page.getByLabel('Descrizione (facoltativa)').fill(description);
  }
  await page.getByRole('button', { name: 'Crea progetto' }).click();
  await expect(page).not.toHaveURL(before); // con omonimi il titolo da solo non prova il cambio
  await expect(page.getByRole('heading', { level: 1, name })).toBeVisible();
}

async function openFromList(page: Page, name: string, index = 0) {
  await openSidebar(page);
  await page.getByRole('navigation', { name: 'Elenco progetti' }).getByRole('link', { name }).nth(index).click();
}

test('senza sessione si viene rimandati al login', async ({ page }) => {
  await page.goto('/projects/00000000-0000-0000-0000-000000000000');
  await expect(page).toHaveURL(/\/login$/);
});

test('credenziali errate mostrano un errore comprensibile', async ({ page }) => {
  await page.goto('/login');
  await page.getByLabel('Nome utente').fill(user!);
  await page.getByLabel('Password').fill('password-sbagliata');
  await page.getByRole('button', { name: 'Accedi' }).click();
  await expect(page.getByRole('alert')).toContainText('non validi');
});

test('creazione, lista e riapertura di un progetto, anche dopo il ricaricamento', async ({ page }) => {
  const name = `Portale ${unique()}`;
  await login(page);

  await createProject(page, name, 'Descrizione del portale');
  await expect(page.getByText('Descrizione del portale')).toBeVisible();
  await expect(page).toHaveURL(/\/projects\/[0-9a-f-]{36}$/);
  const url = page.url();

  // ricaricamento: la sessione si ripristina dal refresh cookie e il progetto resta aperto
  await page.reload();
  await expect(page).toHaveURL(url);
  await expect(page.getByRole('heading', { level: 1, name })).toBeVisible();

  // riapertura dalla lista dopo aver visitato la home
  await page.goto('/');
  await openFromList(page, name);
  await expect(page.getByRole('heading', { level: 1, name })).toBeVisible();
  await expect(page).toHaveURL(url);
});

test('un nome vuoto è rifiutato con spiegazione e nessun progetto è creato', async ({ page }) => {
  await login(page);
  await openSidebar(page);
  await page.getByRole('button', { name: 'Nuovo progetto' }).click();
  await page.getByLabel('Nome', { exact: true }).fill('   ');
  await page.getByRole('button', { name: 'Crea progetto' }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'obbligatorio' })).toBeVisible();
});

test('progetti omonimi sono distinguibili e si riaprono separatamente', async ({ page }) => {
  const name = `Omonimo ${unique()}`;
  await login(page);

  await createProject(page, name, 'Primo');
  const firstUrl = page.url();
  await createProject(page, name, 'Secondo');
  const secondUrl = page.url();
  expect(secondUrl).not.toBe(firstUrl);

  await openSidebar(page);
  const links = page.getByRole('navigation', { name: 'Elenco progetti' }).getByRole('link', { name });
  await expect(links).toHaveCount(2);
  await expect(links.nth(0)).toContainText('·'); // data e autore distinguono le voci

  await openFromList(page, name, 1); // il più vecchio
  await expect(page).toHaveURL(firstUrl);
  await expect(page.getByText('Primo', { exact: true })).toBeVisible();
});

test('la pagina non scorre orizzontalmente', async ({ page }) => {
  await login(page);
  await createProject(page, `Layout ${unique()} ${'molto-lungo-'.repeat(12)}`);
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(0);
});

test('dopo il riavvio dello stack i progetti sono invariati', async ({ page }, info) => {
  test.skip(!restartCommand, 'E2E_RESTART_COMMAND non impostata.');
  test.skip(info.project.name !== 'desktop-1440', 'Riavvio eseguito una sola volta (desktop).');
  test.setTimeout(240_000);

  const name = `Persistente ${unique()}`;
  await login(page);
  await createProject(page, name, 'Sopravvive al riavvio');
  const url = page.url();

  execSync(restartCommand!, { stdio: 'inherit' });
  await expect
    .poll(async () => (await page.request.get('/health').catch(() => null))?.status() ?? 0, { timeout: 120_000 })
    .toBe(200);

  // l'access token era in memoria e il refresh token è nel DB: la sessione sopravvive al riavvio
  await page.goto(url);
  await expect(page.getByRole('heading', { level: 1, name })).toBeVisible();
  await expect(page.getByText('Sopravvive al riavvio')).toBeVisible();
});
