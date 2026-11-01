import { expect, test, type Page } from '@playwright/test';

// T069 - Registro atomico delle evidenze nel browser, su stack reale (Angular + API + PostgreSQL). Variabili: E2E_BASE_URL, E2E_USER,
// E2E_PASSWORD e
//   E2E_MODEL_MODE=offline  endpoint AI irraggiungibile: il messaggio e il lavoro di estrazione si salvano, il lavoro passa per coda e
//                           backoff fino a Non riuscito con errore comprensibile; il retry crea un nuovo lavoro; nessuna evidenza inventata;
//   E2E_MODEL_MODE=real     DGX reale (inferenze seriali): estrazione completata, frase originale con la citazione evidenziata, rianalisi
//                           con versione candidata e confronto fra versioni.
// Senza E2E_MODEL_MODE le prove risultano ignorate, mai superate.
const user = process.env['E2E_USER'];
const password = process.env['E2E_PASSWORD'];
const mode = process.env['E2E_MODEL_MODE'];

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

/** A 390px la sidebar e' richiudibile: la apre se e' chiusa. */
async function openSidebar(page: Page) {
  if (page.viewportSize()!.width >= 992) return;
  const toggle = page.locator('.shell-toggle');
  await expect(toggle).toBeVisible();
  if ((await toggle.getAttribute('aria-expanded')) === 'false') await toggle.click();
}

async function createProject(page: Page, name: string) {
  await openSidebar(page);
  await page.getByRole('button', { name: 'Nuovo progetto' }).click();
  await page.getByLabel('Nome', { exact: true }).fill(name);
  await page.getByRole('button', { name: 'Crea progetto' }).click();
  await expect(page.getByRole('heading', { level: 1, name })).toBeVisible();
}

async function send(page: Page, text: string) {
  await page.getByLabel("Messaggio per l'assistente").fill(text);
  await page.getByRole('button', { name: 'Invia', exact: true }).click();
  await expect(page.getByLabel("Dialogo con l'assistente").getByText(text)).toBeVisible();
}

async function openEvidence(page: Page) {
  await page.getByTestId('tab-evidence').click();
  await expect(page.getByRole('heading', { name: 'Evidenze', exact: true })).toBeVisible();
}

const message = 'Il portale deve inviare un promemoria via email entro 24 ore dalla scadenza; gli SMS restano esclusi.';

test.describe('registro con modello offline (prova d\'errore separata)', () => {
  test.skip(mode !== 'offline', 'E2E_MODEL_MODE=offline non impostata: serve uno stack con endpoint AI irraggiungibile.');

  test('il lavoro attraversa coda e backoff fino a Non riuscito, il retry lo riaccoda e nessuna evidenza compare', async ({ page }, info) => {
    test.skip(info.project.name !== 'desktop-1440', 'Prova eseguita una sola volta (desktop).');
    test.setTimeout(180_000);

    await login(page);
    await createProject(page, `Evidenze offline ${unique()}`);
    await send(page, message);
    await openEvidence(page);

    const row = page.getByTestId('msg-1');
    await expect(row).toBeVisible();
    // Il modello irraggiungibile e' un errore transitorio: backoff e nuovi tentativi, poi Non riuscito con il codice comprensibile.
    await expect(row.getByTestId('state-badge')).toHaveText('Non riuscito', { timeout: 120_000 });
    await expect(row.getByTestId('job-error')).toBeVisible();
    await expect(page.getByTestId('no-items')).toBeVisible();          // nessuna evidenza inventata

    await row.getByTestId('retry').click();
    await expect(row.getByTestId('state-badge')).toHaveText(/In coda|In elaborazione/);
    await expect(page.getByTestId('evidence-command-error')).toHaveCount(0);

    // L'originale resta nella chat, intatto, anche dopo il fallimento dell'estrazione.
    await page.getByTestId('tab-work').click();
    await expect(page.getByLabel("Dialogo con l'assistente").getByText(message)).toBeVisible();
  });
});

test.describe('registro con modello reale', () => {
  test.skip(mode !== 'real', 'E2E_MODEL_MODE=real non impostata: serve uno stack con DGX Spark raggiungibile (inferenze seriali).');

  test('estrazione completata, citazione evidenziata nell\'originale, rianalisi con candidata e confronto', async ({ page }, info) => {
    test.skip(info.project.name !== 'desktop-1440', 'Prova eseguita una sola volta (desktop).');
    test.setTimeout(900_000);

    await login(page);
    await createProject(page, `Evidenze reali ${unique()}`);
    await send(page, message);
    await openEvidence(page);

    const row = page.getByTestId('msg-1');
    await expect(row.getByTestId('state-badge')).toHaveText('Estratto', { timeout: 600_000 });
    const first = page.getByRole('list', { name: 'Evidenze correnti' }).locator('li.ev-row').first();
    await expect(first.getByTestId('ev-statement')).not.toBeEmpty();
    await first.getByTestId('ev-detail').click();
    const original = page.getByTestId('original');
    await expect(original).toBeVisible();
    await expect(original.locator('mark').first()).toBeVisible();      // la citazione e' evidenziata nell'originale
    const quoted = (await original.locator('mark').first().textContent())!.trim();
    expect(message).toContain(quoted);                                 // e' una sottostringa reale del messaggio PM

    await row.getByTestId('reanalyze').click();
    await expect(row.getByTestId('candidates')).toBeVisible({ timeout: 600_000 });
    await row.getByTestId('open-versions').click();
    await expect(page.getByTestId('version')).toHaveCount(2);
    await page.getByTestId('compare').first().click();
    await expect(page.getByTestId('diff-summary')).toBeVisible();
  });
});
