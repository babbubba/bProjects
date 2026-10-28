import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AuthService } from '../auth/auth.service';
import { ContextUsagePanelComponent } from './context-usage-panel.component';
import {
  calculated,
  contextUsage,
  estimated,
  measured,
  metrics,
  unavailable,
} from './context-usage.fixtures';
import { CONTEXT_USAGE_DEBOUNCE_MS } from './context-usage.store';
import { ContextUsage } from './project-api.service';
import { ProjectEventsService, ProjectStreamHandlers } from './project-events.service';

const url = (id: string) => `/api/projects/${id}/context-usage`;

/** Copia di `contextUsage()` con `last` modificato. */
function withLast(over: Partial<NonNullable<ContextUsage['last']>>): ContextUsage {
  const base = contextUsage();
  return { ...base, last: { ...base.last!, ...over } };
}

describe('ContextUsagePanelComponent', () => {
  let fixture: ComponentFixture<ContextUsagePanelComponent>;
  let http: HttpTestingController;
  let el: HTMLElement;
  let handlers: ProjectStreamHandlers;
  const open = vi.fn();

  const settle = async () => {
    await new Promise((resolve) => setTimeout(resolve, 5));
    await fixture.whenStable();
    fixture.detectChanges();
  };
  const q = (id: string) => el.querySelector<HTMLElement>(`[data-testid=${id}]`);
  const text = (id: string) => q(id)?.textContent?.replace(/\s+/g, ' ').trim();

  /** Apre il progetto e risponde al GET del monitor con `usage` (o con un errore). */
  async function load(usage: ContextUsage | 'error', projectId = 'p-1') {
    fixture.componentRef.setInput('projectId', projectId);
    fixture.detectChanges();
    await settle();
    const req = http.expectOne(url(projectId));
    if (usage === 'error') req.flush('offline', { status: 503, statusText: 'Service Unavailable' });
    else req.flush(usage);
    await settle();
  }

  beforeEach(() => {
    open.mockReset();
    open.mockImplementation((_id: string, h: ProjectStreamHandlers) => {
      handlers = h;
      return () => undefined;
    });
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: CONTEXT_USAGE_DEBOUNCE_MS, useValue: 0 },
        { provide: ProjectEventsService, useValue: { open } },
        { provide: AuthService, useValue: { isAuthenticated: signal(true).asReadonly() } },
      ],
    });
    fixture = TestBed.createComponent(ContextUsagePanelComponent);
    http = TestBed.inject(HttpTestingController);
    el = fixture.nativeElement;
  });

  afterEach(() => http.verify());

  it('mostra il valore principale con numeri it-IT e la provenienza', async () => {
    await load(contextUsage());

    expect(text('usage-headline')).toBe('Contesto: 21.256 / 32.768 token — 64,9%');
    expect(text('usage-provenance')).toContain('Prompt misurato');
    expect(text('usage-provenance')).toContain('Chat');
    expect(q('usage-alert')).toBeNull();
    expect(q('usage-error')).toBeNull();
  });

  it('la barra è un progressbar con valori, aria-valuetext e legenda dei quattro segmenti', async () => {
    await load(contextUsage());

    const bar = q('usage-bar')!;
    expect(bar.getAttribute('role')).toBe('progressbar');
    expect(bar.getAttribute('aria-valuemin')).toBe('0');
    expect(bar.getAttribute('aria-valuemax')).toBe('100');
    expect(bar.getAttribute('aria-valuenow')).toBe('64.9');
    expect(bar.getAttribute('aria-label')).toBe('Occupazione del contesto');
    const valueText = bar.getAttribute('aria-valuetext')!;
    expect(valueText).toContain('64,9%');
    expect(valueText).toContain('prompt effettivo 21.256');
    expect(valueText).toContain('output riservato 8.192');
    expect(valueText).toContain('margine tecnico 512');
    expect(valueText).toContain('residuo 2.808');

    const legend = [...el.querySelectorAll('[data-testid=usage-legend] li')].map((li) =>
      li.textContent!.replace(/\s+/g, ' ').trim(),
    );
    expect(legend).toEqual([
      'Prompt effettivo 21.256',
      'Output riservato 8.192',
      'Margine tecnico 512',
      'Residuo 2.808',
    ]);
    const widths = [...bar.querySelectorAll<HTMLElement>('.progress')].map((p) => parseFloat(p.style.width));
    expect(widths).toHaveLength(4);
    expect(widths[0]).toBeCloseTo(64.9, 0);
    expect(widths.reduce((a, b) => a + b, 0)).toBeLessThanOrEqual(100.0001);
  });

  it('non supera mai il 100% della barra se le quote eccedono la capacità', async () => {
    const usage = withLast({
      usedPercent: 100,
      outputReservedTokens: 20000,
      safetyMarginTokens: 5000,
      remainingTokens: unavailable,
      metrics: metrics({ promptTokens: measured(32768), contextWindowTokens: measured(32768) }),
    });
    await load(usage);

    const widths = [...el.querySelectorAll<HTMLElement>('[data-testid=usage-bar] .progress')].map((p) =>
      parseFloat(p.style.width),
    );
    expect(widths.reduce((a, b) => a + b, 0)).toBeLessThanOrEqual(100.0001);
    expect(q('usage-bar')!.getAttribute('aria-valuenow')).toBe('100');
    expect(text('usage-headline')).toContain('32.768 / 32.768 token — 100,0%');
    expect(el.querySelector('[data-segment=remaining]')!.textContent).toContain('n/d');
  });

  it('un prompt stimato è dichiarato come stima, con «≈»', async () => {
    await load(withLast({ metrics: metrics({ promptTokens: estimated(21256), contextWindowTokens: measured(32768) }) }));

    expect(text('usage-headline')).toBe('Contesto: ≈ 21.256 / 32.768 token — ≈ 64,9%');
    expect(text('usage-provenance')).toContain('Prompt stima');
  });

  it('un prompt non disponibile mostra n/d senza cifre né percentuale inventate', async () => {
    await load(withLast({ metrics: metrics({ promptTokens: unavailable, contextWindowTokens: measured(32768) }), usedPercent: null, remainingTokens: unavailable }));

    expect(text('usage-headline')).toBe('Contesto: n/d / 32.768 token — n/d');
    expect(q('usage-bar')!.hasAttribute('aria-valuenow')).toBe(false);
    expect(q('usage-alert')).toBeNull();
  });

  it('avvisa a soglie dichiarate: nulla sotto 80%, attenzione da 80%, critico da 95%', async () => {
    await load(withLast({ usedPercent: 79.9 }));
    expect(q('usage-alert')).toBeNull();

    handlers.onEvent({ name: 'chat.state', data: {} });
    await settle();
    http.expectOne(url('p-1')).flush(withLast({ usedPercent: 80 }));
    await settle();
    expect(q('usage-alert')!.getAttribute('data-level')).toBe('warning');
    expect(q('usage-alert')!.textContent).toContain('80%');

    handlers.onEvent({ name: 'processing.state', data: {} });
    await settle();
    http.expectOne(url('p-1')).flush(withLast({ usedPercent: 95 }));
    await settle();
    expect(q('usage-alert')!.getAttribute('data-level')).toBe('critical');
    expect(q('usage-alert')!.textContent).toContain('95%');
    // Gli avvisi vivono in una regione role=status, aggiornata senza interrompere.
    expect(q('usage-notes')!.getAttribute('role')).toBe('status');
    expect(q('usage-notes')!.getAttribute('aria-live')).toBe('polite');
    expect(q('usage-notes')!.contains(q('usage-alert'))).toBe(true);
  });

  it('indica che la misura è dell\'ultima inferenza se ci sono messaggi più recenti', async () => {
    await load(withLast({ staleReason: 'newer_messages' }));

    expect(text('usage-stale')).toContain("ultima inferenza completata");
    expect(text('usage-headline')).toContain('21.256');
  });

  it('server offline o non verificato: i valori sono le ultime metriche persistite, non lo stato corrente', async () => {
    const base = contextUsage();
    await load({
      ...base,
      runtime: { ...base.runtime, status: 'offline', contextWindowTokens: unavailable },
    });

    expect(text('usage-snapshot')).toContain('non è raggiungibile');
    expect(text('usage-snapshot')).toContain('ultime metriche persistite');
    expect(text('usage-snapshot')).toContain('non lo stato corrente del server');
    // L'ultima misura resta leggibile, con la capacità persistita nella richiesta.
    expect(text('usage-headline')).toBe('Contesto: 21.256 / 32.768 token — 64,9%');

    handlers.onConnected();
    await settle();
    http.expectOne(url('p-1')).flush({ ...base, runtime: { ...base.runtime, status: 'unverified' } });
    await settle();
    expect(text('usage-snapshot')).toContain('non è stato verificato');
  });

  it('finestra del server diversa da quella configurata: avviso di non corrispondenza', async () => {
    const base = contextUsage();
    await load({
      ...base,
      runtime: { ...base.runtime, contextWindowTokens: measured(65536), windowMismatch: true },
    });

    expect(text('usage-snapshot')).toContain('65.536');
    expect(text('usage-snapshot')).toContain('32.768');
    expect(text('usage-snapshot')).toContain('ultime metriche persistite');
  });

  it('server online e coerente: nessun avviso di snapshot', async () => {
    await load(contextUsage());
    expect(q('usage-snapshot')).toBeNull();
  });

  it('last null: stato vuoto onesto, senza barra né cifre di consumo', async () => {
    await load(contextUsage({ last: null, composition: null, compaction: null }));

    expect(text('usage-empty')).toContain('Nessuna richiesta AI ancora');
    expect(q('usage-headline')).toBeNull();
    expect(q('usage-bar')).toBeNull();
  });

  describe('composizione del contesto', () => {
    it('è espandibile con un pulsante accessibile', async () => {
      await load(contextUsage());

      const toggle = q('composition-toggle') as HTMLButtonElement;
      expect(toggle.tagName).toBe('BUTTON');
      expect(toggle.type).toBe('button');
      expect(toggle.getAttribute('aria-expanded')).toBe('false');
      expect(toggle.getAttribute('aria-controls')).toBe('context-usage-composition');
      expect(el.querySelector('#context-usage-composition')).toBe(q('composition'));
      expect(q('composition')!.hidden).toBe(true);

      toggle.click();
      fixture.detectChanges();
      expect(toggle.getAttribute('aria-expanded')).toBe('true');
      expect(q('composition')!.hidden).toBe(false);

      toggle.click();
      fixture.detectChanges();
      expect(q('composition')!.hidden).toBe(true);
    });

    it('rende le voci, la quota non attribuita e il totale in una tabella semantica', async () => {
      await load(contextUsage());

      const table = q('composition')!.querySelector('table')!;
      expect(table.querySelector('caption')?.textContent).toContain('tokenizer del server');
      expect([...table.querySelectorAll('thead th')].map((h) => h.getAttribute('scope'))).toEqual([
        'col',
        'col',
        'col',
      ]);
      const rows = [...table.querySelectorAll('tbody tr, tfoot tr')].map((tr) =>
        [...tr.children].map((c) => c.textContent!.replace(/\s+/g, ' ').trim()),
      );
      expect(rows).toEqual([
        ['Istruzioni di sistema', '1.200', 'misurato'],
        ['Messaggi del PM · 12 messaggi', '9.800', 'misurato'],
        ["Risposte dell'assistente · 11 messaggi", '8.000', 'misurato'],
        ['Riassunto derivato · 40 messaggi', '≈ 1.500', 'stima'],
        ['Quota non attribuita', '756', 'calcolato'],
        ['Totale prompt', '21.256', 'misurato'],
      ]);
      expect(table.querySelectorAll('tbody th[scope=row]').length).toBe(5);
    });

    it('rende qualunque chiave futura (knowledge_base) senza codice per categoria', async () => {
      const base = contextUsage();
      await load({
        ...base,
        composition: {
          ...base.composition!,
          items: [
            ...base.composition!.items,
            { key: 'knowledge_base', label: 'Knowledge base', tokens: measured(3300), messageCount: null },
            { key: 'document_pdf', label: 'Documento PDF', tokens: unavailable, messageCount: null },
          ],
        },
      });

      expect(q('composition')!.querySelector('[data-key=knowledge_base]')!.textContent).toContain('Knowledge base');
      expect(q('composition')!.querySelector('[data-key=knowledge_base]')!.textContent).toContain('3.300');
      expect(q('composition')!.querySelector('[data-key=document_pdf]')!.textContent).toContain('n/d');
    });

    it('non inventa righe fisse: senza voci restano solo quota non attribuita e totale', async () => {
      const base = contextUsage();
      await load({ ...base, composition: { ...base.composition!, items: [] } });

      const keys = [...q('composition')!.querySelectorAll('[data-key]')].map((r) => r.getAttribute('data-key'));
      expect(keys).toEqual(['unattributed', 'total']);
      expect(q('composition')!.textContent).not.toMatch(/PDF|Markdown|knowledge/i);
    });

    it('composizione assente: messaggio onesto', async () => {
      await load(contextUsage({ composition: null }));
      expect(text('composition-empty')).toContain('non disponibile');
    });
  });

  describe('storico e compattazione', () => {
    it('lo storico originale è distinto dal contesto operativo', async () => {
      await load(contextUsage());

      expect(text('usage-history')).toBe('342 messaggi conservati · 180.000 token (misurato)');
      expect(q('usage-details')!.textContent).toContain('non è il contesto inviato al modello');
      // L'headline resta il prompt dell'ultima richiesta, mai la somma dello storico.
      expect(text('usage-headline')).not.toContain('180.000');
    });

    it('storico senza token disponibili: solo il numero di messaggi', async () => {
      await load(contextUsage({ history: { messageCount: 3, originalTokens: unavailable } }));
      expect(text('usage-history')).toBe('3 messaggi conservati');
    });

    it('mostra l\'ultima compattazione con prima/dopo/risparmiati', async () => {
      await load(contextUsage());

      const compaction = text('usage-compaction')!;
      expect(compaction).toContain('40 messaggi riassunti');
      expect(compaction).toContain('Prima 30.000 (misurato)');
      expect(compaction).toContain('Dopo 12.000 (misurato)');
      expect(compaction).toContain('Risparmiati 18.000 (calcolato)');
    });

    it('omette i valori di compattazione non disponibili', async () => {
      const base = contextUsage();
      await load({
        ...base,
        compaction: { ...base.compaction!, tokensBefore: unavailable, tokensAfter: unavailable, tokensSaved: calculated(500) },
      });

      const compaction = text('usage-compaction')!;
      expect(compaction).not.toContain('Prima');
      expect(compaction).not.toContain('Dopo');
      expect(compaction).toContain('Risparmiati 500');
      expect(compaction).not.toContain('n/d');
    });

    it('nessuna compattazione: lo dichiara', async () => {
      await load(contextUsage({ compaction: null }));
      expect(text('usage-compaction-none')).toBe('Nessuna compattazione eseguita.');
    });

    it('riporta il server AI e la sua ultima richiesta con la riga di metriche', async () => {
      await load(contextUsage());

      expect(text('usage-runtime')).toContain('qwen3-32b');
      expect(text('usage-runtime')).toContain('in linea');
      expect(q('usage-details')!.querySelector('app-inference-metrics')).not.toBeNull();
    });
  });

  describe('compatto e accessibile', () => {
    it('è un aside con etichetta e un solo pulsante di espansione dei dettagli', async () => {
      await load(contextUsage());

      const aside = q('context-usage-panel')!;
      expect(aside.tagName).toBe('ASIDE');
      expect(aside.getAttribute('aria-label')).toBe('Monitor del contesto AI');

      const toggle = q('usage-toggle') as HTMLButtonElement;
      expect(toggle.tagName).toBe('BUTTON');
      expect(toggle.getAttribute('aria-expanded')).toBe('false');
      expect(toggle.getAttribute('aria-controls')).toBe('context-usage-details');
      expect(el.querySelector('#context-usage-details')).toBe(q('usage-details'));
      expect(q('usage-details')!.classList.contains('is-open')).toBe(false);
      expect(toggle.getAttribute('tabindex')).toBeNull(); // raggiungibile da tastiera

      toggle.click();
      fixture.detectChanges();
      expect(toggle.getAttribute('aria-expanded')).toBe('true');
      expect(q('usage-details')!.classList.contains('is-open')).toBe(true);
      expect(toggle.textContent).toContain('Nascondi dettagli');
    });

    it('il riepilogo essenziale è sempre visibile anche a dettagli chiusi', async () => {
      await load(contextUsage());

      const details = q('usage-details')!;
      for (const id of ['usage-headline', 'usage-bar', 'usage-legend']) {
        expect(details.contains(q(id))).toBe(false);
      }
      expect(el.querySelector('.usage-live')!.getAttribute('aria-live')).toBe('polite');
    });
  });

  describe('aggiornamento senza refresh', () => {
    it('ricarica su chat.state, processing.state e riconnessione riusando lo stream condiviso', async () => {
      await load(contextUsage());
      expect(open).toHaveBeenCalledTimes(1);

      handlers.onEvent({ name: 'chat.state', data: { status: 'Completed' } });
      await settle();
      http.expectOne(url('p-1')).flush(withLast({ usedPercent: 70, metrics: metrics({ promptTokens: measured(22938) }) }));
      await settle();
      expect(text('usage-headline')).toBe('Contesto: 22.938 / 49.152 token — 70,0%');

      handlers.onEvent({ name: 'processing.state', data: { status: 'Running' } });
      await settle();
      http.expectOne(url('p-1')).flush(contextUsage());
      await settle();

      handlers.onDisconnected();
      handlers.onConnected();
      await settle();
      http.expectOne(url('p-1')).flush(contextUsage());
      await settle();
      expect(open).toHaveBeenCalledTimes(1);
    });

    it('cambio progetto: stato azzerato e risposta tardiva del precedente scartata', async () => {
      await load(contextUsage(), 'p-A');
      q('usage-toggle')!.click();
      fixture.detectChanges();
      expect(q('usage-toggle')!.getAttribute('aria-expanded')).toBe('true');
      handlers.onEvent({ name: 'chat.state', data: {} });
      await settle();
      const lateA = http.expectOne(url('p-A'));

      fixture.componentRef.setInput('projectId', 'p-B');
      fixture.detectChanges();
      await settle();
      expect(q('usage-loading')).not.toBeNull(); // nessun valore del progetto A
      http
        .expectOne(url('p-B'))
        .flush(contextUsage({ runtime: { ...contextUsage().runtime, modelId: 'modello-B' } }));
      await settle();
      expect(text('usage-runtime')).toContain('modello-B');

      lateA.flush(contextUsage({ runtime: { ...contextUsage().runtime, modelId: 'modello-A' } }));
      await settle();

      expect(text('usage-runtime')).toContain('modello-B');
      expect(text('usage-runtime')).not.toContain('modello-A');
      expect(q('usage-toggle')!.getAttribute('aria-expanded')).toBe('false');
    });

    it('GET fallito (backend o DGX offline): messaggio sobrio, nessun numero', async () => {
      await load('error');

      expect(text('usage-error')).toContain('Monitor del contesto non disponibile');
      expect(q('usage-headline')).toBeNull();
      expect(q('usage-bar')).toBeNull();
      expect(el.querySelector('[data-testid=context-usage-panel]')!.textContent).not.toMatch(/\d{2,}/);
    });

    it('dopo un errore i vecchi numeri non restano e il pannello si riprende', async () => {
      await load(contextUsage());
      expect(q('usage-headline')).not.toBeNull();

      handlers.onEvent({ name: 'chat.state', data: {} });
      await settle();
      http.expectOne(url('p-1')).flush('x', { status: 500, statusText: 'Server Error' });
      await settle();
      expect(q('usage-error')).not.toBeNull();
      expect(q('usage-headline')).toBeNull();

      handlers.onEvent({ name: 'chat.state', data: {} });
      await settle();
      http.expectOne(url('p-1')).flush(contextUsage());
      await settle();
      expect(q('usage-error')).toBeNull();
      expect(text('usage-headline')).toContain('21.256');
    });
  });
});
