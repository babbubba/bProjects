import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { EvidenceApiService } from './evidence-api.service';

const P = 'p-1';
const BASE = `/api/projects/${P}`;
const KEY = '8b6a4f3c-2d1e-4a7b-9c5d-0e1f2a3b4c5d';

describe('EvidenceApiService', () => {
  let api: EvidenceApiService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    api = TestBed.inject(EvidenceApiService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('reads status, versions, detail and diff from the documented paths', async () => {
    const status = api.getStatus(P);
    http.expectOne(`${BASE}/evidence/status`).flush({ messages: [], queue: {} });
    await status;

    const versions = api.getVersions(P, 'm-1');
    http.expectOne(`${BASE}/messages/m-1/evidence/versions`).flush({ versions: [] });
    await versions;

    const detail = api.getEvidence(P, 'e-1');
    http.expectOne(`${BASE}/evidence/e-1`).flush({ evidence: {}, messages: [], relations: [] });
    await detail;

    const diff = api.compareVersions(P, 'v-1', 'v-2');
    http.expectOne(`${BASE}/evidence/versions/v-1/compare/v-2`).flush({ entries: [] });
    await diff;

    const batch = api.getBatch(P, 'b-1');
    http.expectOne(`${BASE}/evidence/batches/b-1`).flush({ id: 'b-1' });
    await batch;
  });

  it('sends only the non empty filters and unwraps items', async () => {
    const list = api.listEvidence(P, { messageId: 'm-1', kind: 'Requirement', polarity: '' });
    const req = http.expectOne((r) => r.url === `${BASE}/evidence`);
    expect(req.request.params.get('messageId')).toBe('m-1');
    expect(req.request.params.get('kind')).toBe('Requirement');
    expect(req.request.params.has('polarity')).toBe(false);
    expect(req.request.params.has('certainty')).toBe(false);
    req.flush({ items: [{ id: 'e-1' }] });
    expect(await list).toEqual([{ id: 'e-1' }]);
  });

  it('sends the Idempotency-Key on message reanalysis and job retry', async () => {
    const re = api.reanalyzeMessage(P, 'm-1', KEY);
    const reReq = http.expectOne(`${BASE}/messages/m-1/evidence/reanalyses`);
    expect(reReq.request.method).toBe('POST');
    expect(reReq.request.headers.get('Idempotency-Key')).toBe(KEY);
    reReq.flush({ id: 'j-1' }, { status: 202, statusText: 'Accepted' });
    expect((await re).id).toBe('j-1');

    const retry = api.retryJob(P, 'j-1', KEY);
    const retryReq = http.expectOne(`${BASE}/evidence/jobs/j-1/retry`);
    expect(retryReq.request.method).toBe('POST');
    expect(retryReq.request.headers.get('Idempotency-Key')).toBe(KEY);
    retryReq.flush({ id: 'j-2' }, { status: 202, statusText: 'Accepted' });
    await retry;
  });

  it('sends the Idempotency-Key on project reanalysis and backfill', async () => {
    const re = api.reanalyzeProject(P, KEY);
    const reReq = http.expectOne(`${BASE}/evidence/reanalyses`);
    expect(reReq.request.headers.get('Idempotency-Key')).toBe(KEY);
    reReq.flush({ id: 'b-1', kind: 'Reanalysis' }, { status: 202, statusText: 'Accepted' });
    expect((await re).kind).toBe('Reanalysis');

    const fill = api.startBackfill(P, KEY);
    const fillReq = http.expectOne(`${BASE}/evidence/backfills`);
    expect(fillReq.request.headers.get('Idempotency-Key')).toBe(KEY);
    fillReq.flush({ id: 'b-2', kind: 'Backfill' }, { status: 202, statusText: 'Accepted' });
    await fill;
  });

  it('posts the promotion body with the expected state revision and no Idempotency-Key', async () => {
    const promo = api.promote(P, 'm-1', {
      versionId: 'v-2',
      expectedStateRevision: 3,
      reason: 'più completa',
    });
    const req = http.expectOne(`${BASE}/messages/m-1/evidence/promotions`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({
      versionId: 'v-2',
      expectedStateRevision: 3,
      reason: 'più completa',
    });
    expect(req.request.headers.has('Idempotency-Key')).toBe(false);
    req.flush({ messageId: 'm-1', stateRevision: 4, currentVersionId: 'v-2', promotion: null });
    expect((await promo).stateRevision).toBe(4);
  });

  it('reads the evidence selection and manages coverage reports', async () => {
    const sel = api.getEvidenceSelection(P, 'a-1');
    http.expectOne(`${BASE}/analyses/a-1/evidence-selection`).flush({ items: [], evidences: [] });
    await sel;

    const create = api.createCoverageReport(P, 'a-1', { revisionId: 'r-1', draftId: 'd-1' });
    const createReq = http.expectOne(`${BASE}/analyses/a-1/coverage-reports`);
    expect(createReq.request.method).toBe('POST');
    expect(createReq.request.body).toEqual({ revisionId: 'r-1', draftId: 'd-1' });
    createReq.flush({ id: 'c-1' }, { status: 201, statusText: 'Created' });
    await create;

    const list = api.listCoverageReports(P, 'a-1');
    http.expectOne(`${BASE}/analyses/a-1/coverage-reports`).flush({ items: [{ id: 'c-1' }] });
    expect(await list).toEqual([{ id: 'c-1' }] as never);

    const one = api.getCoverageReport(P, 'a-1', 'c-1');
    http.expectOne(`${BASE}/analyses/a-1/coverage-reports/c-1`).flush({ id: 'c-1' });
    await one;
  });

  it('encodes ids placed in the path', async () => {
    const detail = api.getEvidence('p/1', 'e 1');
    http.expectOne('/api/projects/p%2F1/evidence/e%201').flush({});
    await detail;
  });
});
