import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { expect, test, type Page } from '@playwright/test';
import { FakeParsing } from './support/fake-parsing';
import { FlowStack, completeDocumentAnalysis, documentSources, evidenceEnvelopes, installFlowRoutes, type DocSource } from './support/fake-document-flow';
import { coverageReport, installEvidenceRoutes } from './support/fake-long-context';
import { checkpoint } from './support/a11y';
import { documentFixturesDir } from './support/documents';

// 003 T035 (E2E-OFFLINE, browser): sorgente `retrieval` dello stadio Gap nella vista di copertura, con API simulate nel browser (fake-stack), a 1440 e 390 px,
// con la tastiera per i comandi e axe (WCAG A+AA) sulle fasi. Il backend e' simulato: la forma JSON e' quella di DocumentCoverage3ReportView + `retrieval` e di
// RetrievalRegistryView (003 T032/T033); NON prova il recupero ne' il calcolo dei campi (provati da RetrievalGapFlowTests e dai test di coverage).
// Casi: (1) run con sorgente recupero (candidati per Gap, assenza non provata); (2) run degradato, riordino non disponibile; (3) embedding non disponibile;
// (4) indice non pronto. In (2)-(4) nessuna anteprima, conferma o bozza: mai un risultato parziale presentato come valido.

const P = 'p-1';
const mediaTypes: Record<string, string> = {
  pdf: 'application/pdf', docx: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', md: 'text/markdown', txt: 'text/plain',
};
const files = ['md-basic.md', 'txt-conflict-a.txt', 'pdf-text.pdf', 'docx-basic.docx'];

let fake: FlowStack;
let sources: DocSource[];
let consoleErrors: string[];

test.beforeEach(async ({ page }) => {
  fake = new FlowStack();
  const parsing = new FakeParsing();
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

async function activate(page: Page, locator: ReturnType<Page['getByTestId']>) {
  await expect(async () => {
    await locator.focus({ timeout: 1_000 });
    await expect(locator).toBeFocused({ timeout: 500 });
  }).toPass({ timeout: 8_000 });
  await page.keyboard.press('Enter');
}

async function openProject(page: Page) {
  await page.goto('/');
  await page.getByLabel('Nome utente').fill('pm');
  await page.getByLabel('Password').fill('non-un-segreto');
  await page.getByRole('button', { name: 'Accedi' }).click();
  await expect(page.getByRole('heading', { name: 'Seleziona o crea un progetto' })).toBeVisible();
  await page.goto(`/projects/${P}`);
  await expect(page.getByRole('heading', { level: 1, name: 'Aurora' })).toBeVisible();
}

async function prepareDocuments(page: Page) {
  await activate(page, page.getByTestId('tab-documents'));
  for (const name of files) {
    await page.getByTestId('docs-file').setInputFiles({ name, mimeType: mediaTypes[name.split('.').pop()!], buffer: readFileSync(join(documentFixturesDir, name)) });
    await activate(page, page.getByTestId('docs-upload'));
    await expect(page.getByTestId('docs-item').filter({ hasText: name }).getByTestId('docs-status')).toHaveText('Pronto');
  }
  sources = documentSources(fake);
  await activate(page, page.getByTestId('tab-work'));
}

async function noHorizontalScroll(page: Page) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true);
}

const gap = (id: string, over: Record<string, unknown>) => ({
  gapEvidenceId: id, gapEvidenceVersionId: `v-${id}`, sourceSnapshotHash: 'a'.repeat(64), supportingEvidenceIds: [], openItemId: null, openConflictId: null,
  originBatchId: 'gap-stage', ...over,
});

const registry = (analysisId: string) => ({
  analysisId, searchBasis: 'retrieval', outcome: 'Package', retrievalComplete: true, degraded: false, causes: [], policyVersion: 'retrieval-policy-1',
  gaps: [
    {
      gapEvidenceId: 'gap-1', query: 'Quanti giorni di preavviso sono previsti?',
      candidates: [
        { rank: 1, documentName: 'md-basic.md', documentVersion: 1, section: 'Recesso', rangeStart: 120, rangeEnd: 310, lexicalRank: 1, denseRank: 2, rrfRank: 1, origin: 'ranked', inPackage: true, excludedReason: null },
        { rank: 2, documentName: 'txt-conflict-a.txt', documentVersion: 1, section: '', rangeStart: 0, rangeEnd: 140, lexicalRank: null, denseRank: 1, rrfRank: 2, origin: 'ranked', inPackage: true, excludedReason: null },
        { rank: 3, documentName: 'docx-basic.docx', documentVersion: 2, section: 'Allegato A', rangeStart: 40, rangeEnd: 500, lexicalRank: 3, denseRank: null, rrfRank: 3, origin: 'ranked', inPackage: false, excludedReason: 'budget' },
      ],
    },
  ],
});

test.describe('[E2E fake] recupero dei passaggi: copertura e mancato completamento', () => {
  test('coverage-3 con sorgente recupero: candidati per Gap, assenza non provata, nessuna presentazione di completezza', async ({ page }, info) => {
    fake.addPm(P, 'Analizza i documenti del progetto.');
    await openProject(page);
    await prepareDocuments(page);
    await activate(page, page.getByTestId('analyze'));
    await expect(page.getByTestId('run-state')).toBeVisible();
    const runId = fake.project().runs[0]['id'] as string;
    await completeDocumentAnalysis(page, fake, runId, sources);
    await expect(page.getByTestId('preview')).toBeVisible();

    await page.route(`**/api/projects/${P}/analyses/${runId}/retrieval-registry`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(registry(runId)) }));
    const n = sources.length;
    const stored: Record<string, unknown>[] = [];
    await installEvidenceRoutes(page, () => evidenceEnvelopes(sources), {
      onCreate: () => ({
        status: 201,
        body: coverageReport({
          analysisId: runId, revisionId: 'rev-1', expected: n, represented: n, portionsExpected: 4, portionsProcessed: 4, id: 'cov-v7', checkerVersion: 'coverage-3',
          summaryExtra: {
            expectedGaps: 1, assessedGaps: 1, answeredGaps: 0, openGaps: 1, unassessedGaps: 0, absenceProvenGaps: 0, absenceUnprovenGaps: 1, answeredWithOpenConflictGaps: 0,
          },
          extra: {
            aiRevisionId: 'rev-1', gapAssessmentsHash: 'b'.repeat(64),
            gapAssessments: [gap('gap-1', { outcome: 'Open', reason: 'NoAnswer', rationale: 'Nessun riscontro fra i passaggi recuperati.', searchBasis: 'retrieval', retrievalComplete: true, absenceProven: false, answeredWithOpenConflict: false })],
            retrieval: {
              searchBasis: 'retrieval', retrievalComplete: true, degraded: false, policyVersion: 'retrieval-policy-1', gaps: 1, candidates: 3, inPackage: 2, excludedForBudget: 1,
              noEvidenceForBlock: 0, alwaysIncluded: 0, alwaysIncludedInPackage: 0,
            },
          },
        }),
      }),
      stored,
    });

    await activate(page, page.getByTestId('tab-evidence'));
    await activate(page, page.getByTestId('verify-coverage'));
    await expect(page.getByTestId('report-meta')).toContainText('coverage-3');
    const gaps = page.getByTestId('gap-assessments');
    await expect(gaps.getByTestId('retrieval-summary')).toContainText("non sull'intero ledger");
    await expect(gaps.getByTestId('retrieval-summary')).toContainText('3 candidati');
    await expect(gaps.getByTestId('retrieval-complete')).toContainText('non è una prova di completezza');
    await expect(gaps.getByTestId('gap-absence')).toHaveText('⚠ Assenza non provata');
    await expect(gaps.getByTestId('gap-search-basis')).toContainText('base «retrieval»');
    await expect(gaps.getByTestId('gap-derived-counters')).toContainText('Assenza provata 0 · Assenza non provata 1');

    const details = gaps.getByTestId('gap-candidates');
    await details.locator('summary').focus();
    await page.keyboard.press('Enter');
    await expect(details.getByTestId('gap-candidate')).toHaveCount(3);
    await expect(details.getByTestId('gap-candidate').first()).toContainText('md-basic.md');
    await expect(details.getByTestId('gap-candidate').first()).toContainText('sezione «Recesso»');
    await expect(details.getByTestId('gap-candidate').first()).toContainText('nel contesto del modello');
    await expect(details.getByTestId('gap-candidate').nth(2)).toContainText('escluso per budget di contesto');
    expect(await gaps.innerText()).not.toMatch(/\b(completato|completata)\b/i);
    await details.scrollIntoViewIfNeeded();
    await checkpoint(page, `T035 ${info.project.name}: coverage-3 con sorgente recupero e candidati per Gap`);
    await noHorizontalScroll(page);
    expect(consoleErrors).toEqual([]);
  });

  for (const [title, code, text] of [
    ['riordino non disponibile (modalità degradata)', 'reranker_unavailable', 'Il servizio di riordino dei candidati non è disponibile'],
    ['embedding non disponibile', 'embedding_unavailable', 'Il servizio di embedding non è disponibile'],
    ['indice di ricerca non pronto', 'retrieval_index_not_ready', "L'indice di ricerca delle fonti non è pronto"],
  ] as const) {
    test(`run Failed per ${title}: causa tipizzata, azione leggibile, nessuna anteprima né bozza`, async ({ page }, info) => {
      fake.addPm(P, 'Analizza i documenti del progetto.');
      await openProject(page);
      await prepareDocuments(page);
      await activate(page, page.getByTestId('analyze'));
      await expect(page.getByTestId('run-state')).toBeVisible();
      const runId = fake.project().runs[0]['id'] as string;
      await fake.send(page, 'processing.state', fake.setRun(P, runId, { status: 'Failed', completedAt: '2030-01-01T10:06:00Z', error: { code, message: `${code}: dettaglio server` } }));
      const error = page.getByTestId('run-error');
      await expect(error).toContainText(text);
      await expect(error).toContainText('essuna valutazione dei Gap è stata presentata');
      for (const id of ['preview', 'confirm', 'generate', 'draft']) await expect(page.getByTestId(id)).toHaveCount(0);
      expect(fake.project().analysisId).toBeNull();
      expect((await error.innerText()).toLowerCase()).not.toMatch(/\b(analisi completata|completato con successo)\b/);
      await checkpoint(page, `T035 ${info.project.name}: ${code}`);
      await noHorizontalScroll(page);
      expect(consoleErrors).toEqual([]);
    });
  }
});
