"""Scarico e verifica dei pesi (T021). Usato SOLO durante la build autorizzata dell'immagine: mai a runtime.

  python fetch_models.py download <dir>                    scarica layout, tableformer e tokenizer locale in <dir>
  python fetch_models.py manifest <dir> <model-artifacts>  (re)genera model-artifacts.json dai file scaricati
  python fetch_models.py verify <dir> <model-artifacts>    confronta hash/size con model-artifacts.json (exit 1 se diverso)

OCR, VLM, formule, classificatori di immagini e altri modelli opzionali NON vengono scaricati.
"""
import hashlib
import json
import re
import shutil
import sys
from pathlib import Path

TOKENIZER_REPO = "sentence-transformers/all-MiniLM-L6-v2"
TOKENIZER_REVISION = "1110a243fdf4706b3f48f1d95db1a4f5529b4d41"
TOKENIZER_DIR = "tokenizer--all-MiniLM-L6-v2"
UNUSED_DIRS = ["docling-project--docling-layout-heron-onnx"]  # scaricata da download_models ma non usata dalla pipeline PDF (verificato offline)
TOKENIZER_FILES = ["tokenizer.json", "tokenizer_config.json", "special_tokens_map.json", "vocab.txt", "config.json"]


def sha256_file(p: Path) -> str:
    h = hashlib.sha256()
    with p.open("rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def tracked_files(root: Path):
    return sorted(p for p in root.rglob("*") if p.is_file() and ".cache" not in p.relative_to(root).parts
                  and p.name not in (".gitattributes", ".gitignore"))


def download(out: Path) -> None:
    from docling.utils.model_downloader import download_models
    from huggingface_hub import snapshot_download

    out.mkdir(parents=True, exist_ok=True)
    download_models(output_dir=out, with_layout=True, with_tableformer=True, with_tableformer_v2=False, with_code_formula=False,
                    with_picture_classifier=False, with_smolvlm=False, with_granitedocling=False, with_rapidocr=False,
                    with_easyocr=False, with_nemotron_ocr=False)
    for d in UNUSED_DIRS:
        shutil.rmtree(out / d, ignore_errors=True)
    snapshot_download(TOKENIZER_REPO, revision=TOKENIZER_REVISION, allow_patterns=TOKENIZER_FILES, local_dir=out / TOKENIZER_DIR)


def _license(repo_dir: Path) -> str | None:
    readme = repo_dir / "README.md"
    if readme.is_file():
        m = re.search(r"^license:\s*(\S+)", readme.read_text(encoding="utf-8", errors="replace"), re.M)
        if m:
            return m.group(1)
    return None


def _revision(repo_dir: Path) -> str | None:
    for meta in sorted((repo_dir / ".cache" / "huggingface" / "download").rglob("*.metadata")):
        first = meta.read_text(encoding="utf-8", errors="replace").splitlines()
        if first and re.fullmatch(r"[0-9a-f]{40}", first[0]):
            return first[0]
    return None


def manifest(root: Path, target: Path) -> None:
    sources = []
    for d in sorted(p for p in root.iterdir() if p.is_dir()):
        rev = _revision(d) or (TOKENIZER_REVISION if d.name == TOKENIZER_DIR else None)
        sources.append({"dir": d.name, "repo": TOKENIZER_REPO if d.name == TOKENIZER_DIR else d.name.replace("--", "/", 1),
                        "revision": rev, "license": _license(d) or ("apache-2.0" if d.name == TOKENIZER_DIR else None)})
    files = [{"path": p.relative_to(root).as_posix(), "size": p.stat().st_size, "sha256": sha256_file(p)} for p in tracked_files(root)]
    target.write_text(json.dumps({"schema": "model-artifacts-1", "downloadOnlyAtBuild": True, "ocr": False, "vlm": False,
                                  "sources": sources, "files": files}, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def verify(root: Path, target: Path) -> int:
    expected = {f["path"]: f for f in json.loads(target.read_text(encoding="utf-8"))["files"]}
    actual = {p.relative_to(root).as_posix(): p for p in tracked_files(root)}
    problems = [f"mancante: {k}" for k in expected if k not in actual] + [f"non registrato: {k}" for k in actual if k not in expected]
    for k, p in actual.items():
        if k in expected and (p.stat().st_size != expected[k]["size"] or sha256_file(p) != expected[k]["sha256"]):
            problems.append(f"hash/size diversi: {k}")
    for line in problems:
        print(line, file=sys.stderr)
    print(f"model-artifacts: {len(expected)} file attesi, {len(problems)} discrepanze")
    return 1 if problems else 0


if __name__ == "__main__":
    cmd = sys.argv[1]
    if cmd == "download":
        download(Path(sys.argv[2]))
    elif cmd == "manifest":
        manifest(Path(sys.argv[2]), Path(sys.argv[3]))
    elif cmd == "verify":
        sys.exit(verify(Path(sys.argv[2]), Path(sys.argv[3])))
    else:
        sys.exit("comando sconosciuto")
