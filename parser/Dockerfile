# syntax=docker/dockerfile:1
# Parser document-parse-1: Python 3.12 + Docling 2.129.0, CPU, offline a runtime. Pesi e dipendenze sono scaricati SOLO in questa build.
# Build:   docker build -t bprojects-parser -f parser/Dockerfile parser
# Test:    docker build --target test -t bprojects-parser-test parser
FROM python:3.12-slim@sha256:aa943ce411423c7582288f18707ff66eddcbaeb510502d747a72aeb34f30d159 AS base
ENV PIP_NO_CACHE_DIR=1 PIP_DISABLE_PIP_VERSION_CHECK=1 PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1

FROM base AS builder
RUN python -m venv /opt/venv
ENV PATH=/opt/venv/bin:$PATH
WORKDIR /build
COPY requirements.lock ./
# --require-hashes: ogni pacchetto (transitive incluse) deve corrispondere all'hash del lock; --no-deps: nessuna risoluzione implicita.
RUN pip install --require-hashes --no-deps --extra-index-url https://download.pytorch.org/whl/cpu -r requirements.lock
COPY fetch_models.py model-artifacts.json ./
# Unico punto con accesso di rete ai pesi (build autorizzata). La build fallisce se hash/size divergono da model-artifacts.json.
RUN python fetch_models.py download /opt/models \
 && python fetch_models.py verify /opt/models model-artifacts.json \
 && python -c "import shutil,pathlib; [shutil.rmtree(p) for p in pathlib.Path('/opt/models').rglob('.cache')]" \
 && chmod -R a-w /opt/models

FROM base AS runtime
RUN useradd --uid 10001 --no-create-home --shell /usr/sbin/nologin parser
COPY --from=builder /opt/venv /opt/venv
COPY --from=builder /opt/models /opt/models
ENV PATH=/opt/venv/bin:$PATH PARSER_MODELS_DIR=/opt/models HF_HUB_OFFLINE=1 TRANSFORMERS_OFFLINE=1 \
    HF_HUB_DISABLE_TELEMETRY=1 DO_NOT_TRACK=1 DOCLING_ARTIFACTS_PATH=/opt/models PARSER_WORK_DIR=/tmp
WORKDIR /app
COPY requirements.lock model-artifacts.json common.py service.py conversion_process.py converters.py text_sources.py chunk_manifest.py ./
USER 10001
EXPOSE 8000
HEALTHCHECK --interval=15s --timeout=5s --retries=5 --start-period=60s \
  CMD python -c "import urllib.request,sys; sys.exit(0 if urllib.request.urlopen('http://127.0.0.1:8000/health', timeout=4).status == 200 else 1)"
# Una sola istanza/worker: una conversione per volta (il subprocess per conversione e gestito da service.py).
CMD ["uvicorn", "service:app", "--host", "0.0.0.0", "--port", "8000", "--workers", "1", "--no-access-log"]

FROM runtime AS test
USER root
COPY requirements-dev.lock ./
RUN pip install --require-hashes --no-deps -r requirements-dev.lock
COPY pyproject.toml ./
COPY tests ./tests
USER 10001
ENV FIXTURES_DIR=/fixtures/documents
CMD ["python", "-m", "pytest", "-q", "-p", "no:cacheprovider"]
