#!/usr/bin/env node
// SOLO PER IL MANUTENTORE (non consegnare questo file al revisore).
// Costruisce una distribuzione ISOLATA per il revisore: una lista chiusa di file, nessun altro, controllata contro fughe di informazioni.
//   node make-reviewer-distribution.mjs --package kit   --out <cartella>   # kit per il revisore-autore dell'insieme
//   node make-reviewer-distribution.mjs --package sheet --out <cartella>   # scheda di revisione semantica (da consegnare SOLO dopo la consegna dell'insieme)
// Scrive <out>/<nome>.zip e <out>/<nome>.zip.sha256. Se un file contiene un termine vietato, o se manca/avanza un file, si ferma senza scrivere il pacchetto.
import fs from "node:fs";
import path from "node:path";
import crypto from "node:crypto";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const arg = (n) => {
  const i = process.argv.indexOf(n);
  return i >= 0 ? process.argv[i + 1] : undefined;
};
const which = arg("--package");
const out = arg("--out");
if (!["kit", "sheet"].includes(which) || !out) {
  console.error("Uso: node make-reviewer-distribution.mjs --package kit|sheet --out <cartella>");
  process.exit(2);
}

const kitDir = path.join(here, "..", "independent-set-kit");
const PACKAGES = {
  kit: {
    name: "independent-set-kit",
    root: kitDir,
    files: ["README.md", "validate.mjs", "template/cases.json", "template/esempio-a.txt", "template/esempio-b.md"],
    prefix: "independent-set-kit/",
  },
  sheet: {
    name: "scheda-revisione-semantica",
    root: here,
    files: ["neg-45-review-sheet.md"],
    prefix: "",
    rename: { "neg-45-review-sheet.md": "scheda-revisione-semantica.md" },
  },
};
const pkg = PACKAGES[which];

// Termini che indicherebbero informazione sull'esito o sui fallimenti di precedenti valutazioni, o sull'annotazione originale.
const FORBIDDEN = [
  /rerank/i, /riordin/i, /diagnos/i, /T0\d\d/, /\bM[1-5]\b/, /A\/B/, /retrieval-policy/i, /fallim/i, /fallit/i, /Hit@/i, /Recall@/i, /baseline/i,
  /neg-\d+/i, /zefiro|orione|aurora/i, /txt-conflict|txt-negations|md-basic|pm-message/i, /annotazione (originale|preesistente|attuale)/i, /politica di recupero/i,
];

const problems = [];
const staged = [];
if (which === "kit") {
  const present = [];
  const walk = (d, rel = "") => {
    for (const e of fs.readdirSync(d, { withFileTypes: true })) {
      const r = rel ? `${rel}/${e.name}` : e.name;
      if (e.isDirectory()) walk(path.join(d, e.name), r);
      else present.push(r);
    }
  };
  walk(kitDir);
  for (const f of present) if (!pkg.files.includes(f)) problems.push(`file non previsto nel kit: ${f}`);
}

for (const f of pkg.files) {
  const full = path.join(pkg.root, f);
  if (!fs.existsSync(full)) {
    problems.push(`file mancante: ${f}`);
    continue;
  }
  const text = fs.readFileSync(full, "utf8");
  for (const re of FORBIDDEN) {
    const m = text.match(re);
    if (m) problems.push(`${f}: termine vietato «${m[0]}»`);
  }
  staged.push([f, full]);
}

if (problems.length) {
  console.error("DISTRIBUZIONE NON CREATA:\n - " + problems.join("\n - "));
  process.exit(1);
}

fs.mkdirSync(out, { recursive: true });
const staging = fs.mkdtempSync(path.join(out, ".staging-"));
try {
  const top = path.join(staging, pkg.prefix);
  for (const [f, full] of staged) {
    const target = path.join(top, pkg.rename?.[f] ?? f);
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.copyFileSync(full, target);
  }
  const zip = path.resolve(out, `${pkg.name}.zip`);
  fs.rmSync(zip, { force: true });
  // Serve bsdtar (crea zip): su Windows quello di sistema, perché in Git Bash `tar` è GNU tar e non scrive zip. Percorsi relativi alla cartella di uscita.
  const bsdtar = process.platform === "win32" && process.env.SystemRoot && fs.existsSync(path.join(process.env.SystemRoot, "System32", "tar.exe"))
    ? path.join(process.env.SystemRoot, "System32", "tar.exe")
    : "tar";
  const r = spawnSync(bsdtar, ["-a", "-cf", path.basename(zip), "-C", path.basename(staging), "."], { encoding: "utf8", cwd: path.resolve(out) });
  if (r.status !== 0) {
    throw new Error("Creazione dello zip fallita (serve bsdtar): " + (r.stderr || r.error));
  }
  const sha = crypto.createHash("sha256").update(fs.readFileSync(zip)).digest("hex");
  fs.writeFileSync(`${zip}.sha256`, `${sha}  ${path.basename(zip)}\n`);
  console.log(`CREATO ${zip}\nSHA-256 ${sha}\nFile inclusi: ${staged.map(([f]) => pkg.prefix + (pkg.rename?.[f] ?? f)).join(", ")}`);
} finally {
  fs.rmSync(staging, { recursive: true, force: true });
}
