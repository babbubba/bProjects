/**
 * Mini parser Markdown sicuro per la lettura della bozza (T042). Produce un albero di dati, mai HTML: il template
 * Angular lo renderizza con interpolazioni (testo sempre escapato). Non esiste alcun percorso per HTML grezzo,
 * immagini remote (le immagini diventano testo), script o link con schemi diversi da http/https/mailto.
 * I marcatori `[fonte:<id>]` diventano nodi «source» risolti dalla UI.
 */
export interface InlineNode {
  t: 'text' | 'strong' | 'em' | 'code' | 'link' | 'source';
  /** Testo (text, code). */
  v?: string;
  /** Figli (strong, em, link). */
  c?: InlineNode[];
  /** Destinazione sicura (link). */
  href?: string;
  /** Id fonte (source). */
  id?: string;
}

export interface ListItem {
  level: number;
  c: InlineNode[];
}

export interface TableModel {
  head: InlineNode[][];
  rows: InlineNode[][][];
}

export type MdBlock =
  | { t: 'heading'; level: number; c: InlineNode[] }
  | { t: 'paragraph'; c: InlineNode[] }
  | { t: 'list'; ordered: boolean; items: ListItem[] }
  | { t: 'code'; v: string }
  | { t: 'quote'; c: InlineNode[] }
  | { t: 'table'; table: TableModel }
  | { t: 'hr' };

const SAFE_PROTOCOLS = new Set(['http:', 'https:', 'mailto:']);

/** Restituisce l'URL normalizzato solo se lo schema è esplicitamente consentito. */
export function safeHref(raw: string): string | null {
  const value = raw.trim();
  if (!value) return null;
  try {
    const url = new URL(value);
    return SAFE_PROTOCOLS.has(url.protocol) ? url.href : null;
  } catch {
    return null;
  }
}

const inlineSource = [
  '\\[fonte:([A-Za-z0-9-]{1,64})\\]', // 1 fonte
  '`([^`]+)`', // 2 code
  '\\*\\*(.+?)\\*\\*', // 3 strong
  '__(.+?)__', // 4 strong
  '!\\[([^\\]]*)\\]\\(([^)\\s]*)[^)]*\\)', // 5,6 immagine → testo
  '\\[([^\\]]+)\\]\\(([^)\\s]*)(?:\\s+"[^"]*")?\\)', // 7,8 link
  '\\*(?!\\s)(.+?)\\*', // 9 em
  '(?<![A-Za-z0-9])_(?!\\s)(.+?)_(?![A-Za-z0-9])', // 10 em
].join('|');

const MAX_DEPTH = 4;

export function parseInline(source: string, depth = 0): InlineNode[] {
  const nodes: InlineNode[] = [];
  const text = (v: string) => {
    if (v) nodes.push({ t: 'text', v });
  };
  if (depth > MAX_DEPTH) {
    text(source);
    return nodes;
  }
  let last = 0;
  // Istanza locale: le chiamate ricorsive non devono condividere lastIndex.
  const pattern = new RegExp(inlineSource, 'g');
  for (let m = pattern.exec(source); m; m = pattern.exec(source)) {
    text(source.slice(last, m.index));
    last = m.index + m[0].length;
    if (m[1] !== undefined) nodes.push({ t: 'source', id: m[1] });
    else if (m[2] !== undefined) nodes.push({ t: 'code', v: m[2] });
    else if (m[3] !== undefined || m[4] !== undefined)
      nodes.push({ t: 'strong', c: parseInline(m[3] ?? m[4], depth + 1) });
    else if (m[6] !== undefined) text(m[5] ? `[immagine: ${m[5]}]` : '[immagine]');
    else if (m[7] !== undefined) {
      const href = safeHref(m[8]);
      const children = parseInline(m[7], depth + 1);
      if (href) nodes.push({ t: 'link', href, c: children });
      else nodes.push(...children);
    } else nodes.push({ t: 'em', c: parseInline(m[9] ?? m[10], depth + 1) });
  }
  text(source.slice(last));
  return nodes;
}

const listPattern = /^(\s*)([-*+]|\d+[.)])\s+(.*)$/;
const separatorPattern = /^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$/;
const hrPattern = /^\s{0,3}([-*_])(\s*\1){2,}\s*$/;

function splitRow(line: string): string[] {
  let row = line.trim();
  if (row.startsWith('|')) row = row.slice(1);
  if (row.endsWith('|')) row = row.slice(0, -1);
  return row.split('|').map((cell) => cell.trim());
}

export function parseMarkdown(markdown: string): MdBlock[] {
  const lines = markdown.replace(/\r\n?/g, '\n').split('\n');
  const blocks: MdBlock[] = [];
  let i = 0;
  const startsBlock = (line: string, next: string | undefined) =>
    /^\s{0,3}(#{1,6}\s|```|>)/.test(line) ||
    hrPattern.test(line) ||
    listPattern.test(line) ||
    (line.includes('|') && next !== undefined && separatorPattern.test(next));

  while (i < lines.length) {
    const line = lines[i];
    if (!line.trim()) {
      i++;
      continue;
    }
    if (/^\s{0,3}```/.test(line)) {
      const body: string[] = [];
      i++;
      while (i < lines.length && !/^\s{0,3}```/.test(lines[i])) body.push(lines[i++]);
      i++; // fence di chiusura (o fine documento)
      blocks.push({ t: 'code', v: body.join('\n') });
      continue;
    }
    const heading = /^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$/.exec(line);
    if (heading) {
      blocks.push({ t: 'heading', level: heading[1].length, c: parseInline(heading[2]) });
      i++;
      continue;
    }
    if (hrPattern.test(line)) {
      blocks.push({ t: 'hr' });
      i++;
      continue;
    }
    if (line.includes('|') && i + 1 < lines.length && separatorPattern.test(lines[i + 1])) {
      const head = splitRow(line).map((cell) => parseInline(cell));
      i += 2;
      const rows: InlineNode[][][] = [];
      while (i < lines.length && lines[i].trim() && lines[i].includes('|')) {
        rows.push(splitRow(lines[i++]).map((cell) => parseInline(cell)));
      }
      blocks.push({ t: 'table', table: { head, rows } });
      continue;
    }
    if (/^\s{0,3}>/.test(line)) {
      const body: string[] = [];
      while (i < lines.length && /^\s{0,3}>/.test(lines[i])) {
        body.push(lines[i++].replace(/^\s{0,3}>\s?/, ''));
      }
      blocks.push({ t: 'quote', c: parseInline(body.join(' ')) });
      continue;
    }
    const first = listPattern.exec(line);
    if (first) {
      const ordered = /\d/.test(first[2]);
      const items: ListItem[] = [];
      while (i < lines.length) {
        const m = listPattern.exec(lines[i]);
        if (m) {
          items.push({
            level: Math.min(3, Math.floor(m[1].replace(/\t/g, '  ').length / 2)),
            c: parseInline(m[3]),
          });
          i++;
        } else if (lines[i].trim() && /^\s+\S/.test(lines[i]) && items.length) {
          const last = items[items.length - 1];
          last.c = [...last.c, ...parseInline(' ' + lines[i].trim())];
          i++;
        } else break;
      }
      blocks.push({ t: 'list', ordered, items });
      continue;
    }
    const paragraph: string[] = [line.trim()];
    i++;
    while (i < lines.length && lines[i].trim() && !startsBlock(lines[i], lines[i + 1])) {
      paragraph.push(lines[i++].trim());
    }
    blocks.push({ t: 'paragraph', c: parseInline(paragraph.join(' ')) });
  }
  return blocks;
}
