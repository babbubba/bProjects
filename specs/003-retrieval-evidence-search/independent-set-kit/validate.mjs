#!/usr/bin/env node
// Validatore e congelatore dell'insieme di valutazione "ab-set-1". Richiede solo Node >= 18, nessuna dipendenza.
// Uso:  node validate.mjs <cartella-insieme> [--allow-small] [--freeze]
//   --allow-small  non applica le quote (solo per il template di esempio)
//   --freeze       se non ci sono errori scrive frozen-manifest.json (SHA-256 del contenuto con a capo LF)
import fs from "node:fs";
import path from "node:path";
import crypto from "node:crypto";

const args = process.argv.slice(2);
const dir = args.find((a) => !a.startsWith("--"));
const small = args.includes("--allow-small");
const freeze = args.includes("--freeze");
if (!dir) {
  console.error("Uso: node validate.mjs <cartella-insieme> [--allow-small] [--freeze]");
  process.exit(2);
}

const errors = [];
const warnings = [];
const err = (m) => errors.push(m);
const warn = (m) => warnings.push(m);

const CATEGORIES = ["explicit-doc", "section-ref", "implicit-unique", "similar-docs", "multi-doc", "ambiguous", "contradiction", "identical-text"];
const QUOTAS = { "explicit-doc": 10, "section-ref": 4, "implicit-unique": 10, "similar-docs": 6, "multi-doc": 6, "ambiguous": 4, "contradiction": 4, "identical-text": 3 };
const TOTAL_MIN = 50;
const TOTAL_MAX = 100;
const RESERVED = new Set(["cases.json", "frozen-manifest.json"]);

const read = (p) => fs.readFileSync(p, "utf8").replace(/^﻿/, "").replace(/\r\n/g, "\n");
const sha = (p) => crypto.createHash("sha256").update(Buffer.from(read(p), "utf8")).digest("hex");
// Stessa suddivisione in blocchi dell'harness: paragrafi separati da una riga vuota.
const blocksOf = (p) => read(p).split("\n\n").map((s) => s.trim()).filter(Boolean);

if (!fs.existsSync(dir) || !fs.statSync(dir).isDirectory()) {
  console.error(`Cartella non trovata: ${dir}`);
  process.exit(2);
}

const setName = path.basename(path.resolve(dir));
const casesPath = path.join(dir, "cases.json");
let doc;
try {
  doc = JSON.parse(read(casesPath));
} catch (e) {
  console.error(`cases.json mancante o non valido: ${e.message}`);
  process.exit(1);
}

// ---- intestazione e dichiarazione di indipendenza
if (doc.schema !== "ab-set-1") err(`"schema" deve essere "ab-set-1"`);
if (doc.name !== setName) err(`"name" (${doc.name}) deve coincidere con il nome della cartella (${setName})`);
if (!/^[A-Za-z0-9][A-Za-z0-9_-]{2,40}$/.test(setName)) err("nome della cartella non valido (lettere, cifre, - e _; 3-41 caratteri)");
if (typeof doc.author !== "string" || doc.author.trim().length < 2) err(`"author" mancante`);
const decl = doc.independenceDeclaration;
if (!decl || decl.notSeenPriorFailures !== true || decl.notSeenPriorResults !== true || typeof decl.statement !== "string" || decl.statement.trim().length < 20) {
  err(`"independenceDeclaration" mancante o incompleta: servono notSeenPriorFailures=true, notSeenPriorResults=true e una "statement" di almeno 20 caratteri`);
}
if (!small && /esempio|template/i.test(doc.author ?? "")) err("l'autore sembra quello del template: indicare il proprio nome");

// ---- documenti
const docFiles = new Map();
for (const f of fs.readdirSync(dir).filter((x) => !RESERVED.has(x)).sort()) {
  const full = path.join(dir, f);
  if (fs.statSync(full).isDirectory()) {
    err(`sottocartelle non ammesse: ${f}`);
    continue;
  }
  if (![".txt", ".md"].includes(path.extname(f).toLowerCase())) {
    err(`formato non ammesso (solo .txt e .md): ${f}`);
    continue;
  }
  if (fs.statSync(full).size > 2 * 1024 * 1024) err(`documento oltre 2 MB: ${f}`);
  docFiles.set(f, blocksOf(full));
}
if (docFiles.size < 2) err(`servono almeno 2 documenti (trovati ${docFiles.size})`);
for (const [f, b] of docFiles) if (b.length < 5 && !small) warn(`${f}: solo ${b.length} blocchi`);

// ---- casi
const cases = doc.scenarios?.[setName];
if (!Array.isArray(cases)) err(`"scenarios.${setName}" deve essere un elenco di query`);
const ids = new Set();
const queries = new Set();
const count = Object.fromEntries(CATEGORIES.map((c) => [c, 0]));
let mentions = 0;
let total = 0;
for (const c of Array.isArray(cases) ? cases : []) {
  total++;
  const at = `query ${c?.id ?? `#${total}`}`;
  if (typeof c.id !== "string" || !/^[A-Za-z0-9_-]{1,32}$/.test(c.id)) {
    err(`${at}: id non valido`);
    continue;
  }
  if (ids.has(c.id)) err(`${at}: id duplicato`);
  ids.add(c.id);
  if (typeof c.query !== "string" || c.query.trim().length < 8 || c.query.length > 300) {
    err(`${at}: la query deve avere 8-300 caratteri`);
  } else {
    const k = c.query.trim().toLowerCase();
    if (queries.has(k)) err(`${at}: query duplicata`);
    queries.add(k);
  }
  if (!CATEGORIES.includes(c.category)) {
    err(`${at}: categoria "${c.category}" non ammessa (${CATEGORIES.join(", ")})`);
    continue;
  }
  count[c.category]++;
  if (typeof c.mentionsDocument !== "boolean") err(`${at}: "mentionsDocument" deve essere true o false`);
  else if (c.mentionsDocument) mentions++;
  if (["explicit-doc", "section-ref"].includes(c.category) && c.mentionsDocument !== true) err(`${at}: la categoria ${c.category} richiede mentionsDocument=true`);
  if (c.category === "implicit-unique" && c.mentionsDocument !== false) err(`${at}: implicit-unique richiede mentionsDocument=false`);
  if (!Array.isArray(c.gold) || c.gold.length === 0) {
    err(`${at}: nessun riscontro (gold)`);
    continue;
  }
  const goldFiles = new Set();
  for (const g of c.gold) {
    if (typeof g.id !== "string" || !g.id) err(`${at}: riscontro senza id`);
    if (!docFiles.has(g.file)) {
      err(`${at}: file del riscontro inesistente (${g.file})`);
      continue;
    }
    if (typeof g.contains !== "string" || g.contains.trim().length < 12) {
      err(`${at}: "contains" troppo corto (minimo 12 caratteri)`);
      continue;
    }
    const needle = g.contains.toLowerCase();
    const hits = docFiles.get(g.file).filter((b) => b.toLowerCase().includes(needle)).length;
    if (hits !== 1) err(`${at}: il testo "${g.contains.slice(0, 40)}" compare in ${hits} blocchi di ${g.file}: deve comparire in UNO solo`);
    goldFiles.add(g.file);
    if (c.category === "identical-text") {
      const others = [...docFiles.entries()].filter(([f, b]) => f !== g.file && b.some((x) => x.toLowerCase().includes(needle))).length;
      if (others < 1) err(`${at}: identical-text richiede che lo stesso testo compaia anche in un altro documento`);
    }
    if (c.category === "section-ref" && path.extname(g.file).toLowerCase() !== ".md") err(`${at}: section-ref richiede un documento .md con titoli`);
  }
  if (["multi-doc", "contradiction"].includes(c.category) && goldFiles.size < 2) err(`${at}: ${c.category} richiede riscontri in almeno 2 documenti`);
  if (["ambiguous", "contradiction"].includes(c.category) && !(typeof c.notes === "string" && c.notes.trim().length >= 20)) {
    err(`${at}: ${c.category} richiede "notes" (almeno 20 caratteri) che spieghi l'ambiguità o il conflitto`);
  }
}

// ---- quote
if (!small) {
  if (total < TOTAL_MIN || total > TOTAL_MAX) err(`servono ${TOTAL_MIN}-${TOTAL_MAX} query (trovate ${total})`);
  for (const [cat, min] of Object.entries(QUOTAS)) if (count[cat] < min) err(`categoria ${cat}: minimo ${min}, trovate ${count[cat]}`);
  if (total > 0 && (mentions / total < 0.2 || mentions / total > 0.8)) err(`le query che citano il documento devono essere fra il 20% e l'80% (sono ${((100 * mentions) / total).toFixed(0)}%)`);
}

console.log(`Insieme "${setName}": ${total} query, ${docFiles.size} documenti, ${[...docFiles.values()].reduce((a, b) => a + b.length, 0)} blocchi.`);
console.log("Per categoria: " + CATEGORIES.map((c) => `${c} ${count[c]}`).join(", ") + `; citano il documento: ${mentions}`);
for (const w of warnings) console.log(`AVVISO  ${w}`);
for (const e of errors) console.log(`ERRORE  ${e}`);
if (errors.length) {
  console.log(`\nNON VALIDO (${errors.length} errori).`);
  process.exit(1);
}

console.log("\nVALIDO.");
if (freeze) {
  const files = fs.readdirSync(dir).filter((f) => f !== "frozen-manifest.json").sort();
  const manifest = { frozenAt: new Date().toISOString().slice(0, 10), set: setName, files: Object.fromEntries(files.map((f) => [f, sha(path.join(dir, f))])) };
  fs.writeFileSync(path.join(dir, "frozen-manifest.json"), JSON.stringify(manifest, null, 2) + "\n", "utf8");
  console.log("CONGELATO: frozen-manifest.json scritto. Non modificare più alcun file dell'insieme; consegnare la cartella intera.");
}
