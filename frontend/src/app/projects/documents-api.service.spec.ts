import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { lastValueFrom, toArray } from 'rxjs';
import {
  DocumentsApiService,
  DocumentVersionView,
  UploadEvent,
  documentFailure,
  fileNameFromDisposition,
  newIdempotencyKey,
} from './documents-api.service';

const P = 'p-1';
const BASE = `/api/projects/${P}`;
const KEY = '8b6a4f3c-2d1e-4a7b-9c5d-0e1f2a3b4c5d';

function version(over: Partial<DocumentVersionView> = {}): DocumentVersionView {
  return {
    id: 'v-1',
    documentId: 'd-1',
    projectId: P,
    version: 1,
    originalName: 'a.md',
    mediaType: 'text/markdown',
    byteLength: 3,
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

describe('DocumentsApiService', () => {
  let api: DocumentsApiService;
  let http: HttpTestingController;
  const file = new File(['abc'], 'a.md', { type: 'text/markdown' });

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    api = TestBed.inject(DocumentsApiService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('lists with cursor and limit, and reads detail and version', async () => {
    const page = api.list(P, 'c1', 20);
    const req = http.expectOne((r) => r.url === `${BASE}/documents`);
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('cursor')).toBe('c1');
    expect(req.request.params.get('limit')).toBe('20');
    req.flush({ items: [], nextCursor: null });
    expect((await page).nextCursor).toBeNull();

    const first = api.list(P);
    const noCursor = http.expectOne((r) => r.url === `${BASE}/documents`);
    expect(noCursor.request.params.has('cursor')).toBe(false);
    noCursor.flush({ items: [], nextCursor: null });
    await first;

    const detail = api.get(P, 'd-1');
    http.expectOne(`${BASE}/documents/d-1`).flush({ documentId: 'd-1', versions: [] });
    await detail;

    const v = api.getVersion(P, 'd-1', 2);
    http.expectOne(`${BASE}/documents/d-1/versions/2`).flush(version({ version: 2 }));
    expect((await v).version).toBe(2);
  });

  it('uploads a new document as multipart with the idempotency key and no AI parameters', async () => {
    const events = lastValueFrom(api.uploadDocument(P, file, '  Capitolato ', KEY).pipe(toArray()));
    const req = http.expectOne(`${BASE}/documents`);
    expect(req.request.method).toBe('POST');
    expect(req.request.headers.get('Idempotency-Key')).toBe(KEY);
    const form = req.request.body as FormData;
    expect([...form.keys()].sort()).toEqual(['displayName', 'file']);
    expect(form.get('displayName')).toBe('Capitolato');
    expect((form.get('file') as File).name).toBe('a.md');
    req.event({ type: 1, loaded: 3, total: 3 });
    req.flush(version(), { status: 201, statusText: 'Created' });

    const result = await events;
    expect(result[0]).toEqual<UploadEvent>({ kind: 'progress', loaded: 3, total: 3 });
    const done = result.at(-1)!;
    expect(done.kind === 'done' && done.replayed).toBe(false);
  });

  it('omits displayName when blank and uploads versions on the document route', async () => {
    const first = lastValueFrom(api.uploadDocument(P, file, ' ', KEY));
    const a = http.expectOne(`${BASE}/documents`);
    expect([...(a.request.body as FormData).keys()]).toEqual(['file']);
    a.flush(version(), { status: 201, statusText: 'Created' });
    await first;

    const second = lastValueFrom(api.uploadVersion(P, 'd-1', file, KEY));
    const b = http.expectOne(`${BASE}/documents/d-1/versions`);
    expect(b.request.headers.get('Idempotency-Key')).toBe(KEY);
    expect([...(b.request.body as FormData).keys()]).toEqual(['file']);
    b.flush(version({ version: 2 }), { status: 201, statusText: 'Created' });
    expect(((await second) as { version: DocumentVersionView }).version.version).toBe(2);
  });

  it('replays the same key after an uncertain outcome and reports the replay (200)', async () => {
    const key = newIdempotencyKey();
    expect(key).toMatch(/^[0-9a-f-]{36}$/);

    const lost = lastValueFrom(api.uploadDocument(P, file, null, key));
    http.expectOne(`${BASE}/documents`).error(new ProgressEvent('error'), { status: 0 });
    const failure = documentFailure(await lost.catch((e) => e));
    expect(failure.kind).toBe('network');
    expect(failure.retrySameKey).toBe(true);

    const retry = lastValueFrom(api.uploadDocument(P, file, null, key));
    const req = http.expectOne(`${BASE}/documents`);
    expect(req.request.headers.get('Idempotency-Key')).toBe(key);
    req.flush(version(), { status: 200, statusText: 'OK' });
    const done = await retry;
    expect(done.kind === 'done' && done.replayed).toBe(true);
  });

  it('downloads the original as a blob and keeps the Content-Disposition file name', async () => {
    const pending = api.download(P, 'd-1', 2, 'fallback.bin');
    const req = http.expectOne(`${BASE}/documents/d-1/versions/2/download`);
    expect(req.request.responseType).toBe('blob');
    req.flush(new Blob(['abc']), {
      headers: { 'Content-Disposition': `attachment; filename="x.md"; filename*=UTF-8''Capitolato%20%C3%A8.md` },
    });
    const result = await pending;
    expect(result.fileName).toBe('Capitolato è.md');
    expect(result.blob.size).toBe(3);

    const plain = api.download(P, 'd-1', 1, 'fallback.bin');
    http.expectOne(`${BASE}/documents/d-1/versions/1/download`).flush(new Blob(['a']));
    expect((await plain).fileName).toBe('fallback.bin');
  });

  it('parses Content-Disposition variants', () => {
    expect(fileNameFromDisposition('attachment; filename="a b.pdf"', 'f')).toBe('a b.pdf');
    expect(fileNameFromDisposition('attachment; filename=a.txt', 'f')).toBe('a.txt');
    expect(fileNameFromDisposition(null, 'f')).toBe('f');
  });

  it('reads the selection with its ETag and writes it with If-Match', async () => {
    const read = api.getSelection(P);
    http
      .expectOne(`${BASE}/source-selection`)
      .flush({ revision: 3, items: [], blockingSources: [] }, { headers: { ETag: '"3"' } });
    const snapshot = await read;
    expect(snapshot.etag).toBe('"3"');
    expect(snapshot.view.revision).toBe(3);

    const items = [{ documentId: 'd-1', included: false, mode: 'LatestReady' as const, versionId: null }];
    const write = api.putSelection(P, snapshot.etag, items);
    const req = http.expectOne(`${BASE}/source-selection`);
    expect(req.request.method).toBe('PUT');
    expect(req.request.headers.get('If-Match')).toBe('"3"');
    expect(req.request.body).toEqual({ items });
    req.flush({ revision: 4, items: [], blockingSources: [] }, { headers: { ETag: '"4"' } });
    expect((await write).etag).toBe('"4"');
  });

  it('never sends prompt, regime or timeout fields', async () => {
    const up = lastValueFrom(api.uploadDocument(P, file, 'n', KEY));
    const req = http.expectOne(`${BASE}/documents`);
    const names = [...(req.request.body as FormData).keys()].join(',');
    expect(names).not.toMatch(/prompt|regime|timeout/i);
    expect(req.request.url).not.toMatch(/prompt|regime|timeout/i);
    expect(req.request.params.keys()).toEqual([]);
    req.flush(version(), { status: 201, statusText: 'Created' });
    await up;
  });

  it.each([
    [409, { code: 'idempotency_conflict' }, 'idempotency_conflict', false],
    [409, { code: 'upload_in_progress' }, 'upload_in_progress', true],
    [409, { code: 'source_selection_conflict', currentRevision: 7 }, 'selection_conflict', false],
    [409, { code: 'other' }, 'conflict', false],
    [413, { code: 'file_too_large' }, 'too_large', false],
    [415, null, 'unsupported_format', false],
    [422, null, 'invalid_input', false],
    [507, { code: 'quota_exceeded' }, 'quota', false],
    [408, { code: 'upload_timeout' }, 'timeout', true],
    [503, { code: 'upload_expired' }, 'unavailable', true],
    [404, null, 'not_found', false],
    [418, null, 'unknown', false],
  ])('maps status %i %j to %s', (status, body, kind, retry) => {
    const f = documentFailure(new HttpErrorResponse({ status, error: body }));
    expect(f.kind).toBe(kind);
    expect(f.retrySameKey).toBe(retry);
    expect(f.message.length).toBeGreaterThan(10);
  });

  it('exposes the current revision on a selection conflict', () => {
    const f = documentFailure(
      new HttpErrorResponse({ status: 409, error: { code: 'source_selection_conflict', currentRevision: 7 } }),
    );
    expect(f.currentRevision).toBe(7);
    expect(documentFailure(new Error('x')).kind).toBe('unknown');
  });
});
