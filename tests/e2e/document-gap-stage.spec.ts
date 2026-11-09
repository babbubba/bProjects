import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { expect, test, type Page } from '@playwright/test';
import { FakeParsing } from './support/fake-parsing';
import { FlowStack, completeDocumentAnalysis, documentSources, evidenceEnvelopes, installFlowRoutes, type DocSource } from './support/fake-document-flow';
import { coverageReport, installEvidenceRoutes } from './support/fake-long-context';
import { checkpoint } from './support/a11y';
import { documentFixturesDir } from './support/documents';

// T149 (E2E-OFFLINE, browser): campi derivati dello stadio Gap (definizione v6) nella vista di copertura, con API simulate nel browser
// (fake-stack), a 1440 e 390 px, con la tastiera per i comandi e axe (WCAG A+AA) sulle fasi. Il backend e' simulato: la forma JSON e'
// quella di DocumentCoverage3ReportView (EvidenceCoverage.cs, T147); NON prova il calcolo dei campi (provato da DocumentGapStageAssemblyTests).

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

async function prepareAndAnalyze(page: Page): Promise<string> {
  await activate(page, page.getByTestId('tab-documents'));
  for (const name of files) {
    await page.getByTestId('docs-file').setInputFiles({ name, mimeType: mediaTypes[name.split('.').pop()!], buffer: readFileSync(join(documentFixturesDir, name)) });
    await activate(page, page.getByTestId('docs-upload'));
    await expect(page.getByTestId('docs-item').filter({ hasText: name }).getByTestId('docs-status')).toHaveText('Pronto');
  }
  sources = documentSources(fake);
  await activate(page, page.getByTestId('tab-work'));
  await activate(page, page.getByTestId('analyze'));
  await expect(page.getByTestId('run-state')).toBeVisible();
  const runId = fake.project().runs[0]['id'] as string;
  await completeDocumentAnalysis(page, fake, runId, sources);
  await expect(page.getByTestId('preview')).toBeVisible();
  return runId;
}

const gap = (id: string, over: Record<string, unknown>) => ({
  gapEvidenceId: id, gapEvidenceVersionId: `v-${id}`, sourceSnapshotHash: 'a'.repeat(64), supportingEvidenceIds: [], openItemId: null, openConflictId: null,
  originBatchId: 'gap-stage', ...over,
});

test.describe('[E2E fake] stadio Gap: campi derivati nella copertura', () => {
  test('coverage-3 v6: assenza provata e non provata, ricerca, guardia sul conflitto, contatori parziali; report v5 invariato', async ({ page }, info) => {
    fake.addPm(P, 'Analizza i documenti del progetto.');
    await openProject(page);
    const runId = await prepareAndAnalyze(page);
    const n = sources.length;
    const stored: Record<string, unknown>[] = [];
    let next: () => { status: number; body: Record<string, unknown> };
    await installEvidenceRoutes(page, () => evidenceEnvelopes(sources), { onCreate: () => next(), stored });
    const base = { analysisId: runId, revisionId: 'rev-1', expected: n, represented: n, portionsExpected: 4, portionsProcessed: 4 };

    // --- v6 con un Gap non valutato: i conteggi derivati sono dichiarati parziali, nessuna presentazione di completezza.
    next = () => ({
      status: 201,
      body: coverageReport({
        ...base, id: 'cov-v6', checkerVersion: 'coverage-3',
        summaryExtra: {
          expectedGaps: 4, assessedGaps: 3, answeredGaps: 1, openGaps: 2, unassessedGaps: 1,
          absenceProvenGaps: 1, absenceUnprovenGaps: 1, answeredWithOpenConflictGaps: 1,
        },
        extra: {
          aiRevisionId: 'rev-1', gapAssessmentsHash: 'b'.repeat(64),
          gapAssessments: [
            gap('gap-1', { outcome: 'Open', reason: 'NoAnswer', rationale: 'Nessun riscontro nel ledger.', searchBasis: 'ledger_exhaustive', retrievalComplete: true, absenceProven: true, answeredWithOpenConflict: false }),
            gap('gap-2', { outcome: 'Open', reason: 'NoAnswer', rationale: 'Nessun riscontro, ma estrazione incompleta.', searchBasis: 'ledger_exhaustive', retrievalComplete: false, absenceProven: false, answeredWithOpenConflict: false }),
            gap('gap-3', { outcome: 'AnsweredInSnapshot', reason: 'Answered', rationale: 'Il documento risponde.', searchBasis: 'ledger_exhaustive', retrievalComplete: true, answeredWithOpenConflict: true }),
          ],
        },
      }),
    });
    await activate(page, page.getByTestId('tab-evidence'));
    await activate(page, page.getByTestId('verify-coverage'));
    await expect(page.getByTestId('report-meta')).toContainText('coverage-3');
    const gaps = page.getByTestId('gap-assessments');
    await expect(gaps.getByTestId('gap-item')).toHaveCount(3);
    await expect(gaps.getByTestId('gap-absence')).toHaveText(['✓ Assenza provata', '⚠ Assenza non provata']);
    await expect(gaps.getByTestId('gap-search-basis').first()).toContainText('intero ledger delle evidenze · recupero completo');
    await expect(gaps.getByTestId('gap-absence-note')).toContainText("l'assenza non è provata");
    await expect(gaps.getByTestId('gap-open-conflict-guard')).toContainText('Risposta documentale con conflitto aperto');
    await expect(gaps.getByTestId('gap-open-conflict-note')).toContainText('non bloccante');
    await expect(gaps.getByTestId('gap-derived-counters')).toContainText('Assenza provata 1 · Assenza non provata 1');
    await expect(gaps.getByTestId('gap-derived-counters')).toContainText('parziale');
    await expect(gaps.getByTestId('gap-unassessed')).toContainText('senza valutazione');
    expect(await gaps.innerText()).not.toMatch(/\b(completa|completato|completata)\b/i);
    await gaps.scrollIntoViewIfNeeded();
    await checkpoint(page, `T149 ${info.project.name}: coverage-3 con campi derivati dello stadio Gap`);

    // --- report v5 (coverage-3 senza campi derivati): nessuna nuova affermazione.
    next = () => ({
      status: 201,
      body: coverageReport({
        ...base, id: 'cov-v5', checkerVersion: 'coverage-3',
        summaryExtra: { expectedGaps: 1, assessedGaps: 1, answeredGaps: 0, openGaps: 1, unassessedGaps: 0 },
        extra: {
          aiRevisionId: 'rev-1', gapAssessmentsHash: 'c'.repeat(64),
          gapAssessments: [gap('gap-9', { outcome: 'Open', reason: 'NoAnswer', rationale: 'Nessuna risposta.' })],
        },
      }),
    });
    await activate(page, page.getByTestId('verify-coverage'));
    await expect(page.getByTestId('gap-assessments').getByTestId('gap-item')).toHaveCount(1);
    for (const id of ['gap-absence', 'gap-search-basis', 'gap-open-conflict-guard', 'gap-derived-counters', 'gap-absence-note']) {
      await expect(page.getByTestId(id)).toHaveCount(0);
    }
    await checkpoint(page, `T149 ${info.project.name}: coverage-3 v5 senza campi derivati`);

    // --- coverage-2 con campi ostili: valutazione non disponibile, mai «zero Gap» e nessun campo derivato interpretato.
    next = () => ({
      status: 201,
      body: coverageReport({
        ...base, id: 'cov-v2', summaryExtra: { absenceProvenGaps: 1, absenceUnprovenGaps: 0, answeredWithOpenConflictGaps: 1 },
        extra: { gapAssessments: [gap('gap-x', { outcome: 'Open', reason: 'NoAnswer', rationale: 'x', absenceProven: true, answeredWithOpenConflict: true })] },
      }),
    });
    await activate(page, page.getByTestId('verify-coverage'));
    await expect(page.getByTestId('report-meta')).toContainText('coverage-2');
    await expect(page.getByTestId('gap-unavailable')).toContainText('non significa che non ci siano Gap');
    for (const id of ['gap-item', 'gap-absence', 'gap-open-conflict-guard', 'gap-derived-counters']) {
      await expect(page.getByTestId(id)).toHaveCount(0);
    }
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true);
    expect(consoleErrors).toEqual([]);
  });
});
