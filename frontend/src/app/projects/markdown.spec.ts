import { parseMarkdown, safeHref } from './markdown';

type Nodes = { t: string; v?: string; id?: string; href?: string }[];

describe('markdown sicuro', () => {
  it('riconosce titoli, elenchi, tabelle, codice ed enfasi come dati', () => {
    const blocks = parseMarkdown(
      '# Titolo\n\nTesto **forte** e *corsivo* e `cod`.\n\n- uno\n- due\n\n1. a\n2. b\n\n| A | B |\n|---|---|\n| 1 | 2 |\n\n```\nx < y\n```\n\n> citazione\n\n---',
    );
    expect(blocks.map((b) => b.t)).toEqual([
      'heading',
      'paragraph',
      'list',
      'list',
      'table',
      'code',
      'quote',
      'hr',
    ]);
    const list = blocks[2] as { ordered: boolean; items: unknown[] };
    expect(list.ordered).toBe(false);
    expect(list.items.length).toBe(2);
    expect((blocks[3] as { ordered: boolean }).ordered).toBe(true);
    const table = (blocks[4] as { table: { head: unknown[]; rows: unknown[] } }).table;
    expect(table.head.length).toBe(2);
    expect(table.rows.length).toBe(1);
    expect((blocks[5] as { v: string }).v).toBe('x < y');
  });

  it('i marcatori [fonte:id] diventano nodi source', () => {
    const [p] = parseMarkdown('Requisito [fonte:abc-123] e [fonte:def].');
    const ids = (p as unknown as { c: Nodes }).c.filter((n) => n.t === 'source').map((n) => n.id);
    expect(ids).toEqual(['abc-123', 'def']);
  });

  it('HTML grezzo resta testo (mai markup)', () => {
    const [p] = parseMarkdown('<script>alert(1)</script> <img src=x onerror=alert(1)>');
    const nodes = (p as unknown as { c: Nodes }).c;
    expect(nodes.every((n) => n.t === 'text')).toBe(true);
    expect(nodes.map((n) => n.v).join('')).toContain('<script>');
  });

  it('immagini remote non diventano mai nodi: solo testo segnaposto', () => {
    const [p] = parseMarkdown('![logo](https://evil.example/x.png)');
    expect((p as unknown as { c: Nodes }).c).toEqual([{ t: 'text', v: '[immagine: logo]' }]);
  });

  it('i link con schemi non sicuri perdono la destinazione, quelli http/https/mailto restano', () => {
    const [p] = parseMarkdown(
      '[x](javascript:alert(1)) [y](data:text/html,a) [z](java\tscript:alert(1)) [ok](https://example.com/a) [m](mailto:a@b.it) [r](/relativo)',
    );
    const links = (p as unknown as { c: Nodes }).c.filter((n) => n.t === 'link').map((n) => n.href);
    expect(links).toEqual(['https://example.com/a', 'mailto:a@b.it']);
  });

  it('safeHref accetta solo schemi consentiti', () => {
    expect(safeHref('javascript:alert(1)')).toBeNull();
    expect(safeHref(' JaVaScRiPt:alert(1)')).toBeNull();
    expect(safeHref('vbscript:x')).toBeNull();
    expect(safeHref('//evil.example')).toBeNull();
    expect(safeHref('https://example.com')).toBe('https://example.com/');
  });
});
