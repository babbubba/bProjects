import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AnalysisSource } from './project-api.service';
import { SourcePart, SourcePartsComponent, partOfSource } from './source-parts.component';

const part = (n: number, over: Partial<SourcePart> = {}): SourcePart => ({
  sourceId: `s-${n}`,
  label: `F${n}`,
  documentId: 'doc-1',
  documentVersionId: 'ver-1',
  preparationId: 'prep-1',
  blockId: `b-${n}`,
  start: 0,
  end: 5,
  quote: `citazione ${n}`,
  ...over,
});

@Component({
  imports: [SourcePartsComponent],
  template: `<app-source-parts projectId="p-1" [parts]="parts()" />`,
})
class Host {
  readonly parts = signal<SourcePart[]>([]);
}

describe('partOfSource', () => {
  const base: AnalysisSource = {
    id: 's-1',
    kind: 'DocumentPassage',
    messageId: null,
    messageRole: null,
    revisionId: null,
    documentId: 'd',
    documentVersionId: 'v',
    preparationId: 'p',
    artifactId: 'a',
    blockId: 'b',
    start: 2,
    end: 9,
    quote: 'testo',
  };

  it('converte una fonte DocumentPassage completa', () => {
    expect(partOfSource(base, 'F1')).toMatchObject({
      sourceId: 's-1',
      blockId: 'b',
      start: 2,
      end: 9,
      quote: 'testo',
    });
  });

  it('rifiuta messaggi, record 1.0 e passaggi incompleti', () => {
    expect(partOfSource({ ...base, kind: 'Message' }, 'F1')).toBeNull();
    const legacy: AnalysisSource = {
      id: 's',
      kind: 'PmRevision',
      messageId: null,
      messageRole: null,
      revisionId: 'r',
    };
    expect(partOfSource(legacy, 'F1')).toBeNull();
    expect(partOfSource({ ...base, blockId: null }, 'F1')).toBeNull();
    expect(partOfSource({ ...base, start: null }, 'F1')).toBeNull();
  });
});

describe('SourcePartsComponent', () => {
  const prepUrl = '/api/projects/p-1/documents/doc-1/versions/ver-1/preparations/prep-1';

  function setup(parts: SourcePart[]) {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(Host);
    fixture.componentInstance.parts.set(parts);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    return { fixture, http, root: fixture.nativeElement as HTMLElement };
  }

  it('una parte sola: nessun «Apri tutte», passaggio solo su richiesta, citazione escapata', async () => {
    const { fixture, http, root } = setup([part(1, { quote: '<img src=x onerror=alert(1)>' })]);
    expect(root.querySelector('[data-testid="open-all-parts"]')).toBeNull();
    expect(root.querySelector('img')).toBeNull();
    expect(root.querySelector('[data-testid="part-quote"]')!.textContent).toContain('<img src=x');
    http.expectNone(prepUrl);
    (root.querySelector('[data-testid="open-part"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    await fixture.whenStable();
    http.expectOne(prepUrl);
    http.verify();
  });

  it('«Apri tutte le parti» apre ogni blocco distinto, senza fonderli', async () => {
    const { fixture, http, root } = setup([part(1), part(2), part(3)]);
    expect(root.textContent).toContain('Citazione in 3 parti');
    (root.querySelector('[data-testid="open-all-parts"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(http.match((r) => r.url === prepUrl).length).toBe(3);
    expect(root.querySelectorAll('app-document-passage').length).toBe(3);
  });
});
