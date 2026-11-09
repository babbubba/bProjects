import { citationGroupsOf, MdBlock, parseMarkdown, splitSourceMarkers } from './markdown';
import { AnalysisSource, DraftSourceMapEntry } from './project-api.service';
import { partOfSource } from './source-parts.component';

/**
 * T088 (DET) — roundtrip delle citazioni lato client: i marcatori atomici `[fonte:<UUID>]` del Markdown della bozza coincidono con le fonti della mappa persistita (frasi e
 * tabelle multipagina, testo non-BMP), i gruppi si ricostruiscono solo dalla mappa (due gruppi possono condividere lo stesso header), le fonti rimosse da un edit non
 * generano parti e le voci storiche restano apribili senza conversione. Funzioni pure: nessuna richiesta di rete.
 */
const HDR = 'a8000000-0000-4000-8000-000000000002';
const P2 = 'a8000000-0000-4000-8000-000000000003';
const P3 = 'a8000000-0000-4000-8000-000000000004';
const MUSIC = 'a8000000-0000-4000-8000-000000000005';
const PM = 'a8000000-0000-4000-8000-000000000006';

const entry = (
  item: string,
  evidence: string | null,
  group: string | null,
  part: number | null,
  source: string,
): DraftSourceMapEntry => ({
  itemId: item,
  evidenceId: evidence,
  citationGroupId: group,
  part,
  sourceId: source,
});

// Due gruppi che condividono l'header: A = (HDR, P2), B = (HDR, P3).
const MAP: DraftSourceMapEntry[] = [
  entry('i-a', 'e-a', 'g-a', 1, P2),
  entry('i-a', 'e-a', 'g-a', 0, HDR),
  entry('i-b', 'e-b', 'g-b', 0, HDR),
  entry('i-b', 'e-b', 'g-b', 1, P3),
  entry('i-m', null, null, null, MUSIC),
  entry('i-pm', null, null, null, PM),
];

const markersOf = (blocks: MdBlock[]): string[] => {
  const out: string[] = [];
  const inline = (nodes: { t: string; id?: string; c?: unknown[] }[]) => {
    for (const n of nodes) {
      if (n.t === 'source' && n.id) out.push(n.id);
      if (n.c) inline(n.c as typeof nodes);
    }
  };
  for (const b of blocks) {
    if (b.t === 'paragraph' || b.t === 'heading' || b.t === 'quote') inline(b.c);
    if (b.t === 'list') b.items.forEach((i) => inline(i.c));
    if (b.t === 'table') {
      b.table.head.forEach(inline);
      b.table.rows.forEach((r) => r.forEach(inline));
    }
  }
  return out;
};

describe('roundtrip delle citazioni della bozza (002 T088)', () => {
  it("i marcatori di una frase restano atomici, uno per fonte, nell'ordine richiesto", () => {
    const blocks = parseMarkdown(`Obiettivo del portale [fonte:${P2}] [fonte:${HDR}].`);

    expect(markersOf(blocks)).toEqual([P2, HDR]);
    expect(
      splitSourceMarkers(`x [fonte:${P2}][fonte:${HDR}]`).filter((s) => s.t === 'source'),
    ).toEqual([
      { t: 'source', id: P2 },
      { t: 'source', id: HDR },
    ]);
  });

  it("una tabella multipagina conserva i marcatori delle celle nell'ordine del documento", () => {
    const markdown = [
      '| Codice | Funzione |',
      '|---|---|',
      `| A1 | Accesso [fonte:${P2}] [fonte:${HDR}] |`,
      `| B7 | Esportazione [fonte:${P3}] |`,
    ].join('\n');

    const blocks = parseMarkdown(markdown);

    expect(blocks.map((b) => b.t)).toEqual(['table']);
    expect(markersOf(blocks)).toEqual([P2, HDR, P3]);
  });

  it('testo non-BMP (coppie surrogate) attraversa il parser senza alterazioni accanto ai marcatori', () => {
    const text = `Annotazioni 𝄞 e 😀 [fonte:${MUSIC}]`;

    const [paragraph] = parseMarkdown(text);

    expect(paragraph.t).toBe('paragraph');
    const nodes = (paragraph as Extract<MdBlock, { t: 'paragraph' }>).c;
    expect(nodes[0]).toEqual({ t: 'text', v: 'Annotazioni 𝄞 e 😀 ' });
    expect(nodes[1]).toEqual({ t: 'source', id: MUSIC });
  });

  it("i gruppi si ricostruiscono dalla mappa persistita: l'header condiviso appartiene a due gruppi distinti con le parti nell'ordine", () => {
    const groups = citationGroupsOf(MAP, HDR);

    expect(groups).toEqual([
      { evidenceId: 'e-a', citationGroupId: 'g-a', sourceIds: [HDR, P2] },
      { evidenceId: 'e-b', citationGroupId: 'g-b', sourceIds: [HDR, P3] },
    ]);
    expect(citationGroupsOf(MAP, P3)).toEqual([
      { evidenceId: 'e-b', citationGroupId: 'g-b', sourceIds: [HDR, P3] },
    ]);
  });

  it('la sequenza globale dei marcatori non definisce i gruppi: senza mappa non si ricostruisce nulla', () => {
    const sequence = markersOf(
      parseMarkdown(`A [fonte:${P2}] [fonte:${HDR}] B [fonte:${HDR}] [fonte:${P3}]`),
    );

    expect(sequence).toEqual([P2, HDR, HDR, P3]);
    expect(citationGroupsOf(undefined, HDR)).toEqual([]);
    expect(citationGroupsOf(null, HDR)).toEqual([]);
    expect(citationGroupsOf([], HDR)).toEqual([]);
  });

  it('le voci senza evidenza o gruppo (PmRevision, registro assente) non formano mai un gruppo', () => {
    expect(citationGroupsOf(MAP, PM)).toEqual([]);
    expect(citationGroupsOf(MAP, MUSIC)).toEqual([]);
    // Una voce a metà (evidenza senza parte) è incoerente e viene ignorata, non completata.
    expect(citationGroupsOf([entry('i', 'e-x', 'g-x', null, HDR)], HDR)).toEqual([]);
  });

  it('i marcatori ripetuti per lo stesso gruppo non duplicano le parti', () => {
    const repeated = [
      ...MAP,
      entry('i-a2', 'e-a', 'g-a', 0, HDR),
      entry('i-a2', 'e-a', 'g-a', 1, P2),
    ];

    expect(citationGroupsOf(repeated, P2)).toEqual([
      { evidenceId: 'e-a', citationGroupId: 'g-a', sourceIds: [HDR, P2] },
    ]);
  });

  describe('parti documentali dopo edit PM e rimozione di fonti', () => {
    const passage = (
      id: string,
      quote: string,
      over: Partial<AnalysisSource> = {},
    ): AnalysisSource => ({
      id,
      kind: 'DocumentPassage',
      messageId: null,
      messageRole: null,
      revisionId: null,
      documentId: 'doc-1',
      documentVersionId: 'ver-1',
      preparationId: 'prep-1',
      artifactId: 'art-1',
      blockId: `b-${id}`,
      start: 0,
      end: quote.length,
      quote,
      ...over,
    });

    it('la quote non-BMP conserva testo e offset UTF-16 [start,end) della fonte storica', () => {
      const quote = 'nota 𝄞 e 😀 fine';
      const part = partOfSource(passage(MUSIC, quote), 'F5')!;

      expect(part.quote).toBe(quote);
      expect(part.end - part.start).toBe(quote.length);
      expect(Array.from(quote).length).toBeLessThan(quote.length); // le coppie surrogate contano due unità UTF-16
    });

    it('una fonte rimossa dalla revisione non genera parte; le altre del gruppo storico restano apribili', () => {
      const sources = [passage(HDR, 'Codice | Descrizione'), passage(P2, 'A1 | Accesso')]; // P3 rimossa dall'edit
      const ids = citationGroupsOf(MAP, P3).flatMap((g) => g.sourceIds);
      const parts = ids.flatMap((id) => {
        const index = sources.findIndex((x) => x.id === id);
        const part = index >= 0 ? partOfSource(sources[index], `F${index + 1}`) : null;
        return part ? [part] : [];
      });

      expect(parts.map((p) => p.sourceId)).toEqual([HDR]);
    });

    it('una fonte di messaggio o PmRevision o un record 1.0 incompleto non ha parte documentale e non viene convertito', () => {
      const message: AnalysisSource = {
        id: PM,
        kind: 'PmRevision',
        messageId: null,
        messageRole: null,
        revisionId: 'rev-0',
      };
      const legacy: AnalysisSource = {
        id: P2,
        kind: 'DocumentPassage',
        messageId: null,
        messageRole: null,
        revisionId: null,
      };

      expect(partOfSource(message, 'F6')).toBeNull();
      expect(partOfSource(legacy, 'F2')).toBeNull();
    });

    it('le quote HTML restano testo: nessun nodo diverso dal testo', () => {
      const blocks = parseMarkdown(`<img src=x onerror=alert(1)> [fonte:${HDR}]`);

      expect(JSON.stringify(blocks)).not.toContain('"t":"link"');
      expect(markersOf(blocks)).toEqual([HDR]);
    });
  });
});
