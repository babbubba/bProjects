import { execFileSync } from 'node:child_process';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { expect, test } from '@playwright/test';

// T106 (SEC), livello [statico compose]: «parser a rete isolata». NON apre il browser e NON avvia alcun container: legge la configurazione risolta di `infra/compose.yaml`
// (`docker compose config`, con un file di variabili fittizie passato con --env-file: infra/.env non viene mai letto ne stampato) e verifica l'isolamento dichiarato del servizio
// `parser`. La prova a runtime (connect/DNS bloccati dal parser su rete interna, 401 senza segreto) e stata eseguita a mano in T026 e registrata in validation/parser.md; il
// comportamento del parser senza rete e provato in `parser/tests/test_offline_conversion.py`. Risultato distinto dai due livelli E2E (fake-stack e stack reale con LLM finto).

const compose = resolve(__dirname, '..', '..', 'infra', 'compose.yaml');

interface Service {
  networks?: Record<string, unknown> | string[];
  ports?: unknown[];
  volumes?: unknown[];
  user?: string;
  read_only?: boolean;
  cap_drop?: string[];
  cap_add?: string[];
  security_opt?: string[];
  pids_limit?: number;
  mem_limit?: string | number;
  cpus?: number | string;
  privileged?: boolean;
  network_mode?: string;
  extra_hosts?: unknown;
  dns?: unknown;
  depends_on?: unknown;
  environment?: Record<string, string | null>;
  devices?: unknown;
  profiles?: string[];
}

/** Ambiente del processo senza le variabili che il compose interpola: i valori vengono SOLO dal file fittizio, mai dall'ambiente o da infra/.env. */
function cleanEnv(): Record<string, string> {
  const env: Record<string, string> = {};
  for (const [k, v] of Object.entries(process.env)) if (v !== undefined && !/^(AI_|AUTH_|POSTGRES_|PARSER_|EXECUTION_|WEB_PORT|COMPOSE_|BPROJECTS_|Ai__|Auth__)/i.test(k)) env[k] = v;
  return env;
}

function resolved(): { networks: Record<string, { internal?: boolean }>; services: Record<string, Service> } {
  const dir = mkdtempSync(join(tmpdir(), 'bp-compose-'));
  try {
    const env = join(dir, 'dummy.env');
    writeFileSync(env, ['POSTGRES_PASSWORD=x', 'AUTH_SIGNING_KEY=y', 'AI_ENDPOINT=http://llm.invalid/v1/', 'AI_MODEL_ID=m', 'AI_API_KEY=k', 'PARSER_INTERNAL_SECRET=z', 'PARSER_BASE_URL=http://parser:8000'].join('\n'));
    const out = execFileSync('docker', ['compose', '--env-file', env, '-f', compose, '--profile', 'parser', 'config', '--format', 'json'], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], env: cleanEnv() });
    return JSON.parse(out);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

const names = (n: Service['networks']): string[] => (Array.isArray(n) ? n : Object.keys(n ?? { default: {} })).sort();

test.describe('[statico compose] parser a rete isolata', () => {
  let config: ReturnType<typeof resolved>;

  test.beforeAll(() => {
    try {
      config = resolved();
    } catch (e) {
      test.skip(true, `docker compose non disponibile: ${(e as Error).message.split('\n')[0]}`);
    }
  });

  test('il parser vive solo su una rete interna senza gateway verso l\'esterno, senza porte, mount, privilegi o dipendenze da DB e modello', () => {
    const parser = config.services['parser'];
    expect(parser, 'servizio parser presente (profilo parser)').toBeDefined();
    expect(parser.profiles).toEqual(['parser']);

    // Rete: una sola, interna (nessun gateway: nessun egress), e il parser non e sulla rete applicativa ne su quella dei dati.
    expect(names(parser.networks)).toEqual(['parser_net']);
    expect(config.networks['parser_net'].internal, 'parser_net e internal: true').toBe(true);
    expect(parser.network_mode).toBeUndefined();
    expect(parser.extra_hosts).toBeUndefined();
    expect(parser.dns).toBeUndefined();

    // Nessuna esposizione, nessun mount (originali, database, chiavi), nessun dispositivo.
    expect(parser.ports ?? []).toEqual([]);
    expect(parser.volumes ?? []).toEqual([]);
    expect(parser.devices).toBeUndefined();

    // Nessun privilegio, filesystem in sola lettura, utente non root, risorse limitate.
    expect(parser.privileged ?? false).toBe(false);
    expect(parser.user).toBe('10001:10001');
    expect(parser.read_only).toBe(true);
    expect(parser.cap_drop).toContain('ALL');
    expect(parser.cap_add ?? []).toEqual([]);
    expect(parser.security_opt).toContain('no-new-privileges:true');
    expect(Number(parser.pids_limit)).toBeGreaterThan(0);
    expect(parser.mem_limit, 'limite di memoria').toBeTruthy();
    expect(Number(parser.cpus)).toBeGreaterThan(0);

    // Nessun segreto o endpoint del resto del sistema: solo il segreto condiviso del protocollo; nessuna dipendenza da db o app.
    expect(Object.keys(parser.environment ?? {})).toEqual(['PARSER_INTERNAL_SECRET']);
    expect(parser.depends_on).toBeUndefined();
  });

  test('solo il backend raggiunge il parser; web e database non sono sulla rete del parser e gli originali sono montati solo nel backend', () => {
    const onParserNet = Object.entries(config.services).filter(([, s]) => names(s.networks).includes('parser_net')).map(([name]) => name).sort();
    expect(onParserNet).toEqual(['app', 'parser']);
    const mounting = (name: string) => JSON.stringify(config.services[name].volumes ?? []);
    expect(mounting('app')).toContain('documents');
    for (const other of Object.keys(config.services).filter((n) => n !== 'app')) expect(mounting(other), `${other} non monta il volume degli originali`).not.toContain('/var/lib/bprojects/documents');
    // Il backend riceve l'indirizzo del parser dalla configurazione (mai un URL scelto dai documenti) e nessun servizio del compose pubblica la porta del parser.
    expect(config.services['app'].environment?.['Parser__BaseUrl']).toBe('http://parser:8000');
    for (const [name, s] of Object.entries(config.services)) expect(JSON.stringify(s.ports ?? []), `${name} non pubblica 8000 del parser`).not.toContain('"target":8000');
    // Il database non e raggiungibile dal parser ne pubblicato: nessuna porta host.
    expect(config.services['db'].ports ?? []).toEqual([]);
  });
});
