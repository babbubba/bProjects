import { expect, test, type Page } from '@playwright/test';

// Secondo incremento browser T023: Login -> Progetto -> Messaggio -> Streaming -> risposta persistita -> riapertura.
// Stack reale (Angular + API + PostgreSQL). Variabili: E2E_BASE_URL, E2E_USER, E2E_PASSWORD e
//   E2E_MODEL_MODE=offline  stack avviato con Ai__Endpoint verso una porta chiusa (nessun DGX): prova d'errore separata;
//   E2E_MODEL_MODE=real     stack con endpoint, modello e chiave DGX reali (servizio sano, nessun'altra coda).
// Senza E2E_MODEL_MODE le prove sul modello risultano ignorate, mai superate.
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

async function reopen(page: Page, name: string) {
  await page.goto('/');
  await openSidebar(page);
  await page.getByRole('navigation', { name: 'Elenco progetti' }).getByRole('link', { name }).first().click();
  await expect(page.getByRole('heading', { level: 1, name })).toBeVisible();
}

async function send(page: Page, text: string) {
  await page.getByLabel("Messaggio per l'assistente").fill(text);
  await page.getByRole('button', { name: 'Invia', exact: true }).click();
}

test.describe('chat con modello offline (prova d\'errore separata)', () => {
  test.skip(mode !== 'offline', 'E2E_MODEL_MODE=offline non impostata: serve uno stack con endpoint AI irraggiungibile.');

  test('l\'originale e\' salvato subito, il turno fallisce con errore comprensibile e il progetto resta utilizzabile', async ({ page }, info) => {
    test.skip(info.project.name !== 'desktop-1440', 'Prova eseguita una sola volta (desktop).');
    test.setTimeout(180_000);

    const name = `Offline ${unique()}`;
    const original = `Voglio un portale clienti ${unique()}`;
    const posts: string[] = [];
    page.on('request', (r) => {
      if (r.method() === 'POST' && /\/api\/projects\/[^/]+\/messages$/.test(new URL(r.url()).pathname)) {
        posts.push(r.postData() ?? '');
      }
    });

    await login(page);
    await createProject(page, name);
    await send(page, original);

    // Salvataggio PM confermato subito, distinto dallo stato del turno AI e prima di qualunque risposta.
    const message = page.locator('article[data-role="user"]').filter({ hasText: original });
    await expect(message.locator('.saved-badge')).toHaveText('Salvato', { timeout: 10_000 });
    await expect(message.locator('.turn-badge')).not.toHaveText('Risposta ricevuta');
    await expect(page.locator('article[data-role="assistant"]')).toHaveCount(0);

    // Il turno termina Failed con un messaggio comprensibile (nessun dettaglio interno), l'originale resta.
    await expect(message.locator('.turn-badge')).toHaveText('Risposta AI non riuscita', { timeout: 150_000 });
    const error = message.locator('.turn-error');
    // Dopo i tentativi con backoff il turno chiude con "tentativi esauriti" (o "modello non raggiungibile").
    await expect(error).toContainText(/tentativi disponibili per rispondere sono esauriti|modello AI non è al momento raggiungibile/);
    await expect(error).not.toContainText(/127\.0\.0\.1|HttpRequestException|Exception/);
    await expect(error).toContainText('non verrà reinviato automaticamente');
    await expect(message.locator('.saved-badge')).toHaveText('Salvato');
    await expect(page.locator('article[data-role="assistant"]')).toHaveCount(0);

    // Nessun reinvio automatico: un solo POST e un solo messaggio PM anche dopo un'attesa.
    await page.waitForTimeout(6_000);
    expect(posts).toHaveLength(1);
    await expect(page.locator('article[data-role="user"]')).toHaveCount(1);

    // Riapertura: l'originale e l'errore del turno sono persistiti, il progetto resta navigabile.
    await page.reload();
    await expect(page.getByRole('heading', { level: 1, name })).toBeVisible();
    const reloaded = page.locator('article[data-role="user"]').filter({ hasText: original });
    await expect(reloaded.locator('.saved-badge')).toHaveText('Salvato');
    await expect(reloaded.locator('.turn-badge')).toHaveText('Risposta AI non riuscita', { timeout: 30_000 });
    await reopen(page, name);
    await expect(page.locator('article[data-role="user"]').filter({ hasText: original })).toHaveCount(1);

    // Il progetto resta utilizzabile: un nuovo messaggio e' accettato e salvato (e fallisce a sua volta, da solo).
    const second = `Secondo messaggio ${unique()}`;
    await send(page, second);
    await expect(page.locator('article[data-role="user"]').filter({ hasText: second }).locator('.saved-badge')).toHaveText('Salvato', { timeout: 10_000 });
    await expect(page.getByLabel("Messaggio per l'assistente")).toBeEnabled();
    await expect(page.locator('article[data-role="user"]')).toHaveCount(2);
  });
});

test.describe('chat con modello reale', () => {
  test.skip(mode !== 'real', 'E2E_MODEL_MODE=real non impostata: serve uno stack con DGX Spark raggiungibile e chiave configurata.');

  test('cinque scambi con streaming visibile, risposte persistite e ripresa dello storico tra sessioni', async ({ page, browser }, info) => {
    test.skip(info.project.name !== 'desktop-1440', 'Prova eseguita una sola volta (desktop).');
    test.setTimeout(6 * 180_000);

    const name = `Chat reale ${unique()}`;
    const messages = [
      'Voglio realizzare un portale clienti chiamato Aurora per gestire i ticket di assistenza: login con email, elenco ticket, allegati.',
      "Gli utenti sono i clienti finali e gli agenti dell'assistenza. Le notifiche via SMS sono escluse dal primo rilascio.",
      'Non so ancora quanti clienti useranno il portale; lo scopriremo dopo il primo trimestre.',
      'La fatturazione nel portale resta fuori perimetro, lo ha deciso la direzione.',
      'Per ora basta cosi: riassumi in due frasi quello che hai capito, senza fare domande.',
    ];

    await login(page);
    await createProject(page, name);

    // Il dialogo non avvia mai l'analisi formale da solo: solo il comando esplicito del PM (Analizza) lo fa.
    const analysisPosts: string[] = [];
    page.on('request', (r) => {
      if (r.method() === 'POST' && /\/analyses$/.test(new URL(r.url()).pathname)) analysisPosts.push(r.url());
    });

    const replies: string[] = [];
    for (const [i, text] of messages.entries()) {
      // Osserva dal browser la comparsa dell'elemento di streaming (testo provvisorio) prima del commit.
      await page.evaluate(() => {
        (window as unknown as { __streamSeen: boolean }).__streamSeen = false;
        new MutationObserver(() => {
          if (document.querySelector('article[data-role="assistant-streaming"] .msg-text')?.textContent?.trim()) {
            (window as unknown as { __streamSeen: boolean }).__streamSeen = true;
          }
        }).observe(document.body, { childList: true, subtree: true, characterData: true });
      });

      const started = Date.now();
      await send(page, text);
      await expect(page.locator('article[data-role="user"] .saved-badge')).toHaveCount(i + 1, { timeout: 10_000 });
      await expect(page.locator('article[data-role="assistant"]')).toHaveCount(i + 1, { timeout: 180_000 });
      expect(Date.now() - started, `scambio ${i + 1} oltre 180s`).toBeLessThanOrEqual(180_000);

      expect(await page.evaluate(() => (window as unknown as { __streamSeen: boolean }).__streamSeen), `scambio ${i + 1}: streaming non osservato`).toBe(true);
      await expect(page.locator('article[data-role="assistant-streaming"]')).toHaveCount(0);
      const reply = (await page.locator('article[data-role="assistant"] .msg-text').last().textContent()) ?? '';
      expect((reply.match(/\?/g) ?? []).length).toBeLessThanOrEqual(1);
      expect(reply).not.toMatch(/<think>/i);
      replies.push(reply);
    }

    // Nessuna analisi formale avviata dal dialogo (il pulsante Analizza esiste ma non e' stato premuto).
    expect(analysisPosts).toEqual([]);
    await expect(page.getByText('Nessuna analisi ancora')).toBeVisible();

    // Riapertura nella stessa sessione e in una sessione nuova (altro contesto, nuovo login).
    await reopen(page, name);
    await expect(page.locator('article[data-role="user"]')).toHaveCount(messages.length);
    await expect(page.locator('article[data-role="assistant"] .msg-text')).toHaveText(replies);

    const context = await browser.newContext({ baseURL: info.project.use.baseURL, viewport: { width: 1440, height: 900 } });
    try {
      const other = await context.newPage();
      await login(other);
      await reopen(other, name);
      await expect(other.locator('article[data-role="user"]')).toHaveCount(messages.length);
      await expect(other.locator('article[data-role="assistant"] .msg-text')).toHaveText(replies);
    } finally {
      await context.close();
    }
  });
});
