import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import {
  DocumentDetailView,
  DocumentListItem,
  DocumentVersionView,
  SourceSelectionView,
} from './documents-api.service';
import { DOCUMENTS_POLL, ProjectDocumentsComponent } from './project-documents.component';

const P = 'p-1';
const BASE = `/api/projects/${P}`;

function item(over: Partial<DocumentListItem> = {}): DocumentListItem {
  return {
    documentId: 'd-1',
    displayName: 'Capitolato',
    createdAt: '2030-01-01T10:00:00Z',
    versionCount: 1,
    latestVersion: {
      id: 'v-1',
      version: 1,
      processingStatus: 'Queued',
      uploadedAt: '2030-01-01T10:00:00Z',
    },
    applicableVersionId: null,
    included: true,
    selectionMode: 'LatestReady',
    availability: 'Unavailable',
    reason: 'preparation_queued',
    ...over,
  };
}

function version(over: Partial<DocumentVersionView> = {}): DocumentVersionView {
  return {
    id: 'v-1',
    documentId: 'd-1',
    projectId: P,
    version: 1,
    originalName: 'capitolato.md',
    mediaType: 'text/markdown',
    byteLength: 2048,
    sha256: 'x',
    uploadedBy: 'u',
    uploadedAt: '2030-01-01T10:00:00Z',
    processingStatus: 'Queued',
    applicationStatus: 'NotApplicable',
    currentPreparationId: null,
    currentEvidenceVersionId: null,
    bundleRevision: 0,
    candidatePreparations: [],
    latestJobId: null,
    included: true,
    selectionMode: 'LatestReady',
    isApplicable: false,
    previousReadyVersionId: null,
    issuesSummary: null,
    allowedActions: ['download'],
    ...over,
  };
}

function selection(revision = 1, over: Partial<SourceSelectionView> = {}): SourceSelectionView {
  return {
    revision,
    items: [
      {
        documentId: 'd-1',
        included: true,
        mode: 'LatestReady',
        versionId: null,
        resolvedVersionId: null,
        preparationId: null,
        availability: 'Unavailable',
      },
    ],
    blockingSources: [],
    ...over,
  };
}

function detail(versions: DocumentVersionView[]): DocumentDetailView {
  return {
    documentId: 'd-1',
    projectId: P,
    displayName: 'Capitolato',
    createdBy: 'u',
    createdAt: '2030-01-01T10:00:00Z',
    versions,
    sourcePreference: { included: true, mode: 'LatestReady', versionId: null },
    availability: 'Unavailable',
    applicableVersionId: null,
  };
}

describe('ProjectDocumentsComponent', () => {
  let fixture: ComponentFixture<ProjectDocumentsComponent>;
  let http: HttpTestingController;
  let el: HTMLElement;
  const pollCfg = { intervalMs: 600000, maxPolls: 60 };

  const settle = async () => {
    await new Promise((resolve) => setTimeout(resolve, 0));
    await fixture.whenStable();
    fixture.detectChanges();
  };

  const q = (testId: string) => el.querySelector<HTMLElement>(`[data-testid="${testId}"]`);

  const flushLoad = async (
    items: DocumentListItem[] = [item()],
    sel: SourceSelectionView = selection(),
    etag = `"${sel.revision}"`,
  ) => {
    http
      .expectOne((r) => r.url === `${BASE}/documents` && r.method === 'GET')
      .flush({ items, nextCursor: null });
    http.expectOne(`${BASE}/source-selection`).flush(sel, { headers: { ETag: etag } });
    await settle();
  };

  const chooseFile = (input: HTMLInputElement, file: File) => {
    Object.defineProperty(input, 'files', { value: [file], configurable: true });
    input.dispatchEvent(new Event('change'));
  };

  beforeEach(async () => {
    TestBed.configureTestingModule({
      imports: [ProjectDocumentsComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: DOCUMENTS_POLL, useValue: pollCfg },
      ],
    });
    pollCfg.intervalMs = 600000;
    pollCfg.maxPolls = 60;
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(ProjectDocumentsComponent);
    fixture.componentRef.setInput('projectId', P);
    el = fixture.nativeElement;
    fixture.detectChanges();
  });

  afterEach(() => http.verify());

  it('shows the real state of each document in Italian, with no AI interface', async () => {
    await flushLoad();
    expect(el.querySelectorAll('[data-testid="docs-item"]')).toHaveLength(1);
    expect(q('docs-status')!.textContent).toContain('In coda');
    expect(q('docs-queued')!.textContent).toContain("l'originale è già archiviato");
    expect(el.textContent).toContain('Non disponibile come fonte');
    expect(el.textContent).not.toMatch(/assistente|chatbot|generato dall/i);
  });

  it('shows an empty state and a readable load error', async () => {
    await flushLoad([], selection(0, { items: [] }));
    expect(q('docs-empty')).not.toBeNull();

    void (fixture.componentInstance as unknown as { refresh(): Promise<void> }).refresh();
    http
      .expectOne((r) => r.url === `${BASE}/documents`)
      .flush({ code: 'x' }, { status: 503, statusText: 'Unavailable' });
    http
      .expectOne(`${BASE}/source-selection`)
      .flush({}, { status: 503, statusText: 'Unavailable' });
    await settle();
    expect(q('docs-load-error')!.textContent).toContain('non disponibile');
  });

  it('uploads with real progress, a new idempotency key and refreshes the list', async () => {
    await flushLoad([], selection(0, { items: [] }));
    chooseFile(q('docs-file') as HTMLInputElement, new File(['abc'], 'a.md'));
    q('docs-upload')!.click();
    await settle();

    const req = http.expectOne(`${BASE}/documents`);
    expect(req.request.method).toBe('POST');
    expect(req.request.headers.get('Idempotency-Key')).toMatch(/^[0-9a-f-]{36}$/);
    expect(q('docs-progress')!.textContent).toContain('Invio del file in corso');
    req.event({ type: 1, loaded: 50, total: 100 });
    await settle();
    expect(q('docs-progress')!.textContent).toContain('50% inviato');
    expect(q('docs-upload')!.hasAttribute('disabled')).toBe(true);

    req.flush(version({ originalName: 'a.md' }), { status: 201, statusText: 'Created' });
    await settle();
    await flushLoad([item()], selection());
    expect(q('docs-notice')!.textContent).toContain('caricato come versione 1');
    expect(q('docs-notice')!.textContent).toContain('in coda');
    expect(el.querySelectorAll('[data-testid="docs-item"]')).toHaveLength(1);
  });

  it('keeps the same idempotency key when retrying the same file after an uncertain outcome', async () => {
    await flushLoad([], selection(0, { items: [] }));
    const file = new File(['abc'], 'a.md', { lastModified: 1 });
    chooseFile(q('docs-file') as HTMLInputElement, file);
    q('docs-upload')!.click();
    await settle();
    const first = http.expectOne(`${BASE}/documents`);
    const key = first.request.headers.get('Idempotency-Key');
    first.error(new ProgressEvent('error'), { status: 0 });
    await settle();
    expect(q('docs-upload-error')!.textContent).toContain('non raggiungibile');

    q('docs-upload')!.click();
    await settle();
    const retry = http.expectOne(`${BASE}/documents`);
    expect(retry.request.headers.get('Idempotency-Key')).toBe(key);
    retry.flush(version(), { status: 200, statusText: 'OK' });
    await settle();
    await flushLoad([item()]);
    expect(q('docs-notice')!.textContent).toContain('già stato caricato');
  });

  it.each([
    [413, { code: 'file_too_large' }, 'dimensione massima'],
    [415, null, 'Formato non supportato'],
    [422, null, 'non è valido'],
    [507, { code: 'quota_exceeded' }, 'quota'],
    [408, { code: 'upload_timeout' }, 'tempo consentito'],
  ])(
    'shows a readable error for upload status %i and drops the key when not retryable',
    async (status, body, text) => {
      await flushLoad([], selection(0, { items: [] }));
      chooseFile(
        q('docs-file') as HTMLInputElement,
        new File(['abc'], 'a.md', { lastModified: 1 }),
      );
      q('docs-upload')!.click();
      await settle();
      http.expectOne(`${BASE}/documents`).flush(body, { status, statusText: 'x' });
      await settle();
      const alert = q('docs-upload-error')!;
      expect(alert.getAttribute('role')).toBe('alert');
      expect(alert.textContent).toContain(text);
      expect(q('docs-upload')!.hasAttribute('disabled')).toBe(false);
    },
  );

  it('requires a file before uploading', async () => {
    await flushLoad([], selection(0, { items: [] }));
    q('docs-upload')!.click();
    await settle();
    expect(q('docs-upload-error')!.textContent).toContain('Scegli un file');
  });

  it('uploads a new version for the chosen document', async () => {
    await flushLoad();
    q('docs-versions')!.click();
    await settle();
    http.expectOne(`${BASE}/documents/d-1`).flush(detail([version()]));
    await settle();

    chooseFile(q('docs-newversion') as HTMLInputElement, new File(['v2'], 'capitolato-v2.md'));
    await settle();
    const req = http.expectOne(`${BASE}/documents/d-1/versions`);
    expect(req.request.headers.get('Idempotency-Key')).toBeTruthy();
    req.flush(version({ version: 2, originalName: 'capitolato-v2.md' }), {
      status: 201,
      statusText: 'Created',
    });
    await settle();
    await flushLoad([item({ versionCount: 2 })]);
    http
      .expectOne(`${BASE}/documents/d-1`)
      .flush(detail([version(), version({ id: 'v-2', version: 2 })]));
    await settle();
    expect(q('docs-notice')!.textContent).toContain('versione 2');
    expect(el.querySelectorAll('[data-testid="docs-version"]')).toHaveLength(2);
  });

  it('excludes a document sending the complete selection with If-Match', async () => {
    await flushLoad();
    (q('docs-include') as HTMLInputElement).click();
    await settle();

    const put = http.expectOne(`${BASE}/source-selection`);
    expect(put.request.method).toBe('PUT');
    expect(put.request.headers.get('If-Match')).toBe('"1"');
    expect(put.request.body).toEqual({
      items: [{ documentId: 'd-1', included: false, mode: 'LatestReady', versionId: null }],
    });
    put.flush(
      selection(2, {
        items: [{ ...selection().items[0], included: false, availability: 'Excluded' }],
      }),
      { headers: { ETag: '"2"' } },
    );
    await settle();
    http
      .expectOne((r) => r.url === `${BASE}/documents`)
      .flush({
        items: [item({ included: false, availability: 'Excluded', reason: 'excluded_by_pm' })],
        nextCursor: null,
      });
    await settle();
    expect(q('docs-notice')!.textContent).toContain('Preferenze di fonte salvate');
    expect(el.textContent).toContain('Esclusa dalle fonti');
    expect((q('docs-include') as HTMLInputElement).checked).toBe(false);
  });

  it('reloads and explains a 409 selection conflict', async () => {
    await flushLoad();
    (q('docs-include') as HTMLInputElement).click();
    await settle();
    http
      .expectOne(`${BASE}/source-selection`)
      .flush(
        { code: 'source_selection_conflict', currentRevision: 5 },
        { status: 409, statusText: 'Conflict' },
      );
    await settle();
    await flushLoad([item({ included: false })], selection(5), '"5"');

    const alert = q('docs-action-error')!;
    expect(alert.getAttribute('role')).toBe('alert');
    expect(alert.textContent).toContain('sono cambiate');
    expect(component().selectionRevision()).toBe(5);
  });

  it('pins a ready version and offers the pin on a not-ready one with its consequence stated', async () => {
    await flushLoad();
    q('docs-versions')!.click();
    await settle();
    http
      .expectOne(`${BASE}/documents/d-1`)
      .flush(
        detail([
          version({ id: 'v-1', version: 1, processingStatus: 'Ready' }),
          version({ id: 'v-2', version: 2, processingStatus: 'Queued' }),
        ]),
      );
    await settle();

    const pins = [...el.querySelectorAll<HTMLButtonElement>('[data-testid="docs-pin"]')];
    expect(pins[0].disabled).toBe(false);
    expect(pins[1].disabled).toBe(false);
    const notes = [...el.querySelectorAll<HTMLElement>('[data-testid="docs-pin-note"]')];
    expect(notes.length).toBe(1); // solo la versione non pronta ha l'avviso, la pronta no
    expect(notes[0].textContent).toContain('Analizza');

    pins[0].click();
    await settle();
    const put = http.expectOne(`${BASE}/source-selection`);
    expect(put.request.body).toEqual({
      items: [{ documentId: 'd-1', included: true, mode: 'Pinned', versionId: 'v-1' }],
    });
    put.flush(
      selection(2, {
        items: [
          { ...selection().items[0], mode: 'Pinned', versionId: 'v-1', availability: 'Ready' },
        ],
      }),
      { headers: { ETag: '"2"' } },
    );
    await settle();
    http
      .expectOne((r) => r.url === `${BASE}/documents`)
      .flush({
        items: [item({ selectionMode: 'Pinned', availability: 'Ready', reason: null })],
        nextCursor: null,
      });
    await settle();
    http
      .expectOne(`${BASE}/documents/d-1`)
      .flush(
        detail([
          version({ id: 'v-1', processingStatus: 'Ready' }),
          version({ id: 'v-2', version: 2 }),
        ]),
      );
    await settle();
    expect(q('docs-pinned')).not.toBeNull();
    expect(el.textContent).toContain('Versione fissata');
  });

  it('lets the PM pin a not-ready version and then states it stays unavailable for Analizza', async () => {
    await flushLoad();
    q('docs-versions')!.click();
    await settle();
    http
      .expectOne(`${BASE}/documents/d-1`)
      .flush(detail([version({ id: 'v-1', version: 1, processingStatus: 'Queued' })]));
    await settle();
    expect(q('docs-pinned-unready')).toBeNull();

    q('docs-pin')!.click();
    await settle();
    const put = http.expectOne(`${BASE}/source-selection`);
    expect(put.request.body).toEqual({
      items: [{ documentId: 'd-1', included: true, mode: 'Pinned', versionId: 'v-1' }],
    });
    put.flush(
      selection(2, {
        items: [
          { ...selection().items[0], mode: 'Pinned', versionId: 'v-1', resolvedVersionId: 'v-1' },
        ],
        blockingSources: [
          {
            documentId: 'd-1',
            displayName: 'Capitolato',
            versionId: 'v-1',
            version: 1,
            status: 'Queued',
            allowedActions: ['exclude'],
          },
        ],
      }),
      { headers: { ETag: '"2"' } },
    );
    await settle();
    http
      .expectOne((r) => r.url === `${BASE}/documents`)
      .flush({
        items: [item({ selectionMode: 'Pinned', availability: 'Unavailable' })],
        nextCursor: null,
      });
    await settle();
    http
      .expectOne(`${BASE}/documents/d-1`)
      .flush(detail([version({ id: 'v-1', version: 1, processingStatus: 'Queued' })]));
    await settle();

    expect(q('docs-pinned')).not.toBeNull();
    const consequence = q('docs-pinned-unready')!;
    expect(consequence.getAttribute('role')).toBe('status');
    expect(consequence.textContent).toContain('non è pronta');
    expect(consequence.textContent).toContain('Analizza');
    expect(consequence.textContent).toContain('escludi');
  });

  it('downloads the original keeping the server file name', async () => {
    await flushLoad();
    q('docs-versions')!.click();
    await settle();
    http.expectOne(`${BASE}/documents/d-1`).flush(detail([version({ version: 1 })]));
    await settle();

    const created = vi.fn(() => 'blob:x');
    const revoked = vi.fn();
    URL.createObjectURL = created;
    URL.revokeObjectURL = revoked;
    let downloadName = '';
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (
      this: HTMLAnchorElement,
    ) {
      downloadName = this.download;
    });

    q('docs-download')!.click();
    await settle();
    http.expectOne(`${BASE}/documents/d-1/versions/1/download`).flush(new Blob(['abc']), {
      headers: { 'Content-Disposition': 'attachment; filename="originale.md"' },
    });
    await settle();
    expect(created).toHaveBeenCalled();
    expect(downloadName).toBe('originale.md');
    click.mockRestore();
  });

  it('reports a failed download without breaking the archive', async () => {
    await flushLoad();
    q('docs-versions')!.click();
    await settle();
    http.expectOne(`${BASE}/documents/d-1`).flush(detail([version()]));
    await settle();
    q('docs-download')!.click();
    await settle();
    http
      .expectOne(`${BASE}/documents/d-1/versions/1/download`)
      .flush(new Blob([]), { status: 503, statusText: 'Unavailable' });
    await settle();
    expect(q('docs-action-error')!.textContent).toContain('Riprova');
  });

  it('shows the preparation failure of a version and the blocking sources', async () => {
    await flushLoad(
      [
        item({
          latestVersion: {
            id: 'v-1',
            version: 1,
            processingStatus: 'Failed',
            uploadedAt: '2030-01-01T10:00:00Z',
          },
        }),
      ],
      selection(1, {
        blockingSources: [
          {
            documentId: 'd-1',
            displayName: 'Capitolato',
            versionId: 'v-1',
            version: 1,
            status: 'Failed',
            allowedActions: [],
          },
        ],
      }),
    );
    expect(q('docs-status')!.textContent).toContain('Preparazione non riuscita');
    expect(q('docs-blocking')!.textContent).toContain('1 documento');

    q('docs-versions')!.click();
    await settle();
    http.expectOne(`${BASE}/documents/d-1`).flush(
      detail([
        version({
          processingStatus: 'Failed',
          issuesSummary: {
            code: 'parse_failed',
            message: 'La preparazione del documento non è riuscita',
          },
        }),
      ]),
    );
    await settle();
    http
      .expectOne(`${BASE}/documents/d-1/versions/1/preparations/latest`)
      .flush({ allowedActions: ['download'], parseJobId: 'job-1' });
    await settle();
    expect(q('docs-issue')!.textContent).toContain('non è riuscita');
    expect(q('docs-retry')).toBeNull();
  });

  it('exposes accessible controls: labelled inputs, live region, expandable versions', async () => {
    await flushLoad();
    expect(el.querySelector('label[for="docs-file"]')).not.toBeNull();
    expect(el.querySelector('label[for="docs-name"]')).not.toBeNull();
    expect(q('docs-live')!.getAttribute('aria-live')).toBe('polite');
    const toggle = q('docs-versions')!;
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    toggle.click();
    await settle();
    http.expectOne(`${BASE}/documents/d-1`).flush(detail([version()]));
    await settle();
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(el.querySelector(`#${toggle.getAttribute('aria-controls')}`)).not.toBeNull();
  });

  it('opens the prepared content of a not-ready version without presenting it as ready (T030)', async () => {
    await flushLoad();
    q('docs-versions')!.click();
    await settle();
    http
      .expectOne(`${BASE}/documents/d-1`)
      .flush(detail([version({ processingStatus: 'Extracting' })]));
    await settle();

    q('docs-read')!.click();
    await settle();
    const prefix = `${BASE}/documents/d-1/versions/1/preparations/latest`;
    http.expectOne(prefix).flush({
      id: 'g-1',
      documentVersionId: 'v-1',
      documentId: 'd-1',
      version: 1,
      generation: 1,
      processingStatus: 'Extracting',
      isReady: false,
      contentState: 'Parsed',
      blockCount: 1,
      retryOfId: null,
      requestedAt: '2030-01-01T10:00:00Z',
      issuesSummary: null,
      issues: [],
      allowedActions: ['download'],
    });
    await settle();
    http
      .expectOne((r) => r.url === `${prefix}/blocks`)
      .flush({
        preparationId: 'g-1',
        processingStatus: 'Extracting',
        isReady: false,
        items: [
          {
            id: 'b-0',
            ref: 'r0',
            ordinal: 0,
            parentId: null,
            type: 'Paragraph',
            text: 'Testo letto',
            headingPath: [],
            page: 1,
            bbox: null,
            lines: null,
            tableCells: null,
          },
        ],
        nextCursor: null,
      });
    await settle();

    expect(q('docs-read')!.getAttribute('aria-expanded')).toBe('true');
    expect(q('passage-block')!.textContent).toContain('Testo letto');
    expect(q('passage-status')!.textContent).not.toContain('Pronto');
    expect(q('passage-notready')).not.toBeNull();
  });

  it('offers no content reader for a version that was never prepared', async () => {
    await flushLoad();
    q('docs-versions')!.click();
    await settle();
    http
      .expectOne(`${BASE}/documents/d-1`)
      .flush(detail([version({ processingStatus: 'NotPrepared' })]));
    await settle();
    expect(q('docs-read')).toBeNull();
  });


  describe('retry parsing (T117)', () => {
    const PREP = `${BASE}/documents/d-1/versions/1/preparations/latest`;
    const RETRY_URL = `${BASE}/document-jobs/job-1/retry`;
    const failedItem = () =>
      item({
        latestVersion: {
          id: 'v-1',
          version: 1,
          processingStatus: 'Failed',
          uploadedAt: '2030-01-01T10:00:00Z',
        },
      });
    const failedVersion = () => version({ processingStatus: 'Failed' });
    const prepView = (actions: string[], over: Record<string, unknown> = {}) => ({
      id: 'g-1',
      documentVersionId: 'v-1',
      documentId: 'd-1',
      version: 1,
      generation: 1,
      processingStatus: 'Failed',
      isReady: false,
      contentState: 'NotParsed',
      blockCount: null,
      retryOfId: null,
      requestedAt: '2030-01-01T10:00:00Z',
      issuesSummary: null,
      issues: [],
      allowedActions: actions,
      parseJobId: 'job-1',
      ...over,
    });
    const retryReply = {
      jobId: 'job-2',
      preparationId: 'g-2',
      generation: 2,
      retryOfJobId: 'job-1',
      retryOfPreparationId: 'g-1',
      status: 'Queued',
      requestedAt: '2030-01-01T11:00:00Z',
      deadlineAt: '2030-01-01T12:00:00Z',
    };

    const openFailed = async (actions: string[], over: Record<string, unknown> = {}) => {
      await flushLoad([failedItem()]);
      q('docs-versions')!.click();
      await settle();
      http.expectOne(`${BASE}/documents/d-1`).flush(detail([failedVersion()]));
      await settle();
      http.expectOne(PREP).flush(prepView(actions, over));
      await settle();
    };
    const retryRequests = () => http.match((r) => r.method === 'POST' && r.url === RETRY_URL);
    const drainGets = () => http.match((r) => r.method === 'GET');
    /** Dopo un conflitto la vista si rilegge con soli GET: lista, dettaglio, preparazione. */
    const flushReread = async (actions: string[]) => {
      http
        .expectOne((r) => r.url === `${BASE}/documents` && r.method === 'GET')
        .flush({ items: [failedItem()], nextCursor: null });
      await settle();
      http.expectOne(`${BASE}/documents/d-1`).flush(detail([failedVersion()]));
      await settle();
      http.expectOne(PREP).flush(prepView(actions));
      await settle();
    };

    it('offre il pulsante solo se allowedActions contiene retry', async () => {
      await openFailed(['download', 'retry']);
      expect(q('docs-retry')).not.toBeNull();
    });

    it('non offre il pulsante senza retry in allowedActions', async () => {
      await openFailed(['download']);
      expect(q('docs-retry')).toBeNull();
    });

    it('non offre il pulsante se manca il job di parsing', async () => {
      await openFailed(['download', 'retry'], { parseJobId: null });
      expect(q('docs-retry')).toBeNull();
    });

    it('non legge la preparazione né offre retry per una versione Ready', async () => {
      await flushLoad();
      q('docs-versions')!.click();
      await settle();
      http
        .expectOne(`${BASE}/documents/d-1`)
        .flush(detail([version({ processingStatus: 'Ready' })]));
      await settle();
      http.expectNone(PREP);
      expect(q('docs-retry')).toBeNull();
    });

    it('invia il retry con Idempotency-Key e mostra il nuovo job collegato con lo stato persistito', async () => {
      await openFailed(['download', 'retry']);
      q('docs-retry')!.click();
      await settle();
      const [req] = retryRequests();
      expect(req.request.headers.get('Idempotency-Key')).toMatch(/^[0-9a-f-]{36}$/);
      req.flush(retryReply, { status: 202, statusText: 'Accepted' });
      await settle();
      http
        .expectOne((r) => r.url === `${BASE}/documents` && r.method === 'GET')
        .flush({ items: [item()], nextCursor: null });
      await settle();
      http
        .expectOne(`${BASE}/documents/d-1`)
        .flush(detail([version({ processingStatus: 'Queued' })]));
      await settle();
      expect(q('docs-notice')!.textContent).toContain('generazione 2');
      expect(q('docs-retry-link')!.textContent).toContain('collegato al precedente');
      expect(q('docs-retry')).toBeNull();
      expect(el.textContent).toContain('In coda');
    });

    it('il doppio click invia una sola richiesta', async () => {
      await openFailed(['download', 'retry']);
      const button = q('docs-retry')!;
      button.click();
      button.click();
      await settle();
      const reqs = retryRequests();
      expect(reqs.length).toBe(1);
      reqs[0].flush(retryReply, { status: 202, statusText: 'Accepted' });
      await settle();
      drainGets();
    });

    it('con la rete persa conserva la stessa Idempotency-Key nel replay', async () => {
      await openFailed(['download', 'retry']);
      q('docs-retry')!.click();
      await settle();
      const [first] = retryRequests();
      const key = first.request.headers.get('Idempotency-Key');
      first.error(new ProgressEvent('error'), { status: 0 });
      await settle();
      expect(q('docs-retry-error')!.textContent).toContain('incerto');
      expect(q('docs-retry')).not.toBeNull();

      q('docs-retry')!.click();
      await settle();
      const [second] = retryRequests();
      expect(second.request.headers.get('Idempotency-Key')).toBe(key);
      second.flush(retryReply, { status: 202, statusText: 'Accepted' });
      await settle();
      drainGets();
    });

    it('409 job_active è un errore accessibile e la vista viene riletta con soli GET', async () => {
      await openFailed(['download', 'retry']);
      q('docs-retry')!.click();
      await settle();
      retryRequests()[0].flush({ code: 'job_active' }, { status: 409, statusText: 'Conflict' });
      await settle();
      await flushReread(['download']);
      expect(q('docs-retry-error')!.textContent).toContain('in corso');
      expect(q('docs-retry-error')!.getAttribute('role')).toBe('alert');
      expect(q('docs-retry')).toBeNull();
      expect(http.match((r) => r.method !== 'GET').length).toBe(0);
    });

    it('409 job_superseded spiega l’esito senza ritentare', async () => {
      await openFailed(['download', 'retry']);
      q('docs-retry')!.click();
      await settle();
      retryRequests()[0].flush({ code: 'job_superseded' }, { status: 409, statusText: 'Conflict' });
      await settle();
      await flushReread(['download']);
      expect(q('docs-retry-error')!.textContent).toContain('più recente');
      expect(q('docs-retry')).toBeNull();
      expect(retryRequests().length).toBe(0);
    });

    it('Aggiorna (refresh) non invia mai un retry', async () => {
      await openFailed(['download', 'retry']);
      q('docs-refresh')!.click();
      await settle();
      expect(http.match((r) => r.method === 'POST').length).toBe(0);
      http.expectOne((r) => r.url === `${BASE}/documents` && r.method === 'GET');
      http.match(() => true);
    });
  });

  describe('aggiornamento automatico degli stati', () => {
    const mountWithPoll = (config: { intervalMs: number; maxPolls: number }) => {
      pollCfg.intervalMs = config.intervalMs;
      pollCfg.maxPolls = config.maxPolls;
    };
    const wait = () => new Promise((r) => setTimeout(r, 40));
    const pollGet = () => http.expectOne((r) => r.url === `${BASE}/documents` && r.method === 'GET');

    it('rilegge con GET finché in lavorazione e si ferma a Ready', async () => {
      mountWithPoll({ intervalMs: 5, maxPolls: 5 });
      await flushLoad([item()]);
      await wait();
      pollGet().flush({
        items: [
          item({
            latestVersion: {
              id: 'v-1',
              version: 1,
              processingStatus: 'Ready',
              uploadedAt: '2030-01-01T10:00:00Z',
            },
            availability: 'Ready',
            reason: null,
          }),
        ],
        nextCursor: null,
      });
      http.expectOne(`${BASE}/source-selection`).flush(selection(2), { headers: { ETag: '"2"' } });
      await settle();
      expect(q('docs-status')!.textContent).toContain('Pronto');
      await wait();
      http.expectNone((r) => r.url === `${BASE}/documents`);
    });

    it('è limitato: dopo maxPolls si ferma, lo dichiara e non invia altre richieste', async () => {
      mountWithPoll({ intervalMs: 5, maxPolls: 2 });
      await flushLoad([item()]);
      for (let i = 0; i < 2; i++) {
        await wait();
        pollGet().flush({ items: [item()], nextCursor: null });
        http.expectOne(`${BASE}/source-selection`).flush(selection(), { headers: { ETag: '"1"' } });
        await settle();
      }
      await wait();
      http.expectNone((r) => r.url === `${BASE}/documents`);
      expect(q('docs-poll-stopped')).not.toBeNull();
    });

    it('non invia mai richieste diverse da GET', async () => {
      mountWithPoll({ intervalMs: 5, maxPolls: 3 });
      await flushLoad([item()]);
      await wait();
      expect(http.match((r) => r.method !== 'GET').length).toBe(0);
      http.match(() => true);
    });
  });

  function component() {
    return fixture.componentInstance as unknown as { selectionRevision(): number | null };
  }
});
