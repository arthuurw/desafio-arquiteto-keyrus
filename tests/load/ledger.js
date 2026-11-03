// Teste de carga moderado contra o ambiente do docker compose (ADR-0023): escritas espalhadas, leituras e uma conta
// disputada. Roda com `docker compose --profile load run --rm k6`, nunca junto da suíte de testes.
import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter } from 'k6/metrics';

const API = __ENV.API_URL || 'http://localhost:8080';
const KEYCLOAK = __ENV.KEYCLOAK_URL || 'http://localhost:8180';
const TOKEN_URL = `${KEYCLOAK}/realms/ledger/protocol/openid-connect/token`;
const RESULTS = __ENV.RESULTS_DIR || '.';
const ANA = '11111111-1111-4111-8111-111111111111';
const ACCOUNTS = 20;

const status409 = new Counter('status_409');
const status422 = new Counter('status_422');
const status503 = new Counter('status_503');
const unexpected = new Counter('unexpected_status');

export const options = {
  setupTimeout: '120s',
  summaryTrendStats: ['avg', 'p(50)', 'p(95)', 'p(99)', 'max'],
  scenarios: {
    writes: {
      executor: 'ramping-vus',
      exec: 'writes',
      startVUs: 0,
      stages: [
        { duration: '30s', target: 10 },
        { duration: '2m30s', target: 10 },
        { duration: '30s', target: 0 },
      ],
    },
    reads: {
      executor: 'ramping-vus',
      exec: 'reads',
      startVUs: 0,
      stages: [
        { duration: '30s', target: 15 },
        { duration: '2m30s', target: 15 },
        { duration: '30s', target: 0 },
      ],
    },
    hot: {
      executor: 'constant-vus',
      exec: 'hot',
      vus: 5,
      startTime: '30s',
      duration: '2m30s',
    },
  },
  // Limiares só informam o resultado: o resumo guarda os números mesmo quando um deles falha.
  thresholds: {
    unexpected_status: ['count<1'],
    'http_req_duration{kind:credit}': ['p(95)<500'],
    'http_req_duration{kind:debit}': ['p(95)<500'],
    'http_req_duration{kind:hot_write}': ['p(95)<2500'],
    'http_req_duration{kind:balance}': ['p(95)<300'],
    'http_req_duration{kind:balance_at}': ['p(95)<300'],
    'http_req_duration{kind:statement}': ['p(95)<300'],
    // Sempre verdadeiros: existem para o resumo trazer a contagem de cada tipo de requisição.
    'http_reqs{kind:credit}': ['count>=0'],
    'http_reqs{kind:debit}': ['count>=0'],
    'http_reqs{kind:hot_write}': ['count>=0'],
    'http_reqs{kind:balance}': ['count>=0'],
    'http_reqs{kind:balance_at}': ['count>=0'],
    'http_reqs{kind:statement}': ['count>=0'],
  },
};

function uuid() {
  return crypto.randomUUID();
}

function form(fields) {
  return Object.entries(fields)
    .map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(v)}`)
    .join('&');
}

function tokenRequest(fields) {
  const res = http.post(TOKEN_URL, form(fields), {
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    tags: { kind: 'token' },
  });
  if (res.status !== 200) {
    // Contado antes de abortar a iteração: uma falha do Keycloak reprova o limiar em vez de sumir do resumo.
    unexpected.add(1, { status: String(res.status), kind: 'token' });
    throw new Error(`token ${fields.grant_type} ${res.status}: ${res.body}`);
  }
  const body = res.json();
  return { value: body.access_token, expiresAt: Date.now() + body.expires_in * 1000 };
}

function serviceToken(client) {
  return tokenRequest({ grant_type: 'client_credentials', client_id: client, client_secret: `${client}_dev` });
}

// Modo delegado: a ana entra pelo channel-web e o channel-app troca o token pelo da Api (ADR-0013).
function delegatedToken() {
  const login = tokenRequest({
    grant_type: 'password', client_id: 'channel-web', username: 'ana', password: 'ana_dev',
  });
  return tokenRequest({
    grant_type: 'urn:ietf:params:oauth:grant-type:token-exchange',
    client_id: 'channel-app',
    client_secret: 'channel-app_dev',
    subject_token_type: 'urn:ietf:params:oauth:token-type:access_token',
    subject_token: login.value,
    audience: 'ledger-api',
  });
}

// Tokens valem 5 min: cada VU guarda os seus e renova com folga, sem ir ao Keycloak a cada iteração.
const tokens = {};
function token(name) {
  const cached = tokens[name];
  if (cached && cached.expiresAt - Date.now() > 60000) {
    return cached.value;
  }
  tokens[name] = name === 'ana' ? delegatedToken() : serviceToken(name);
  return tokens[name].value;
}

function post(path, bearer, body, kind, expected) {
  const res = http.post(`${API}${path}`, JSON.stringify(body), {
    headers: {
      Authorization: `Bearer ${bearer}`,
      'Content-Type': 'application/json',
      'Idempotency-Key': uuid(),
    },
    tags: { kind },
    responseCallback: http.expectedStatuses(...expected),
  });
  record(res, expected);
  return res;
}

function get(path, kind) {
  const res = http.get(`${API}${path}`, {
    headers: { Authorization: `Bearer ${token('backoffice')}` },
    tags: { kind },
  });
  record(res, [200]);
  return res;
}

function record(res, expected) {
  if (res.status === 409) status409.add(1);
  if (res.status === 422) status422.add(1);
  if (res.status === 503) status503.add(1);
  if (!expected.includes(res.status)) unexpected.add(1, { status: String(res.status) });
  check(res, { 'status esperado': (r) => expected.includes(r.status) });
}

function credit(account, kind, expected) {
  return post(`/v1/accounts/${account}/credits`, token('payments-hub'),
    { amount: '10.00', category: 'PIX_IN', externalReference: `load-${uuid()}` }, kind, expected);
}

function debit(account, kind, expected) {
  return post(`/v1/accounts/${account}/debits`, token('ana'),
    { amount: '5.00', category: 'PIX_OUT', externalReference: `load-${uuid()}` }, kind, expected);
}

function pick(list) {
  return list[Math.floor(Math.random() * list.length)];
}

export function setup() {
  // A Api sobe antes de responder pronta: espera a readiness em vez de errar as primeiras chamadas.
  for (let i = 0; i < 60; i++) {
    if (http.get(`${API}/health/ready`, { tags: { kind: 'setup' } }).status === 200) break;
    sleep(1);
  }
  const backoffice = serviceToken('backoffice').value;
  const payments = serviceToken('payments-hub').value;
  const open = () => {
    const res = http.post(`${API}/v1/accounts`, JSON.stringify({ customerId: ANA }), {
      headers: { Authorization: `Bearer ${backoffice}`, 'Content-Type': 'application/json', 'Idempotency-Key': uuid() },
      tags: { kind: 'setup' },
    });
    if (res.status !== 201) throw new Error(`open account ${res.status}: ${res.body}`);
    const id = res.json('id');
    // Saldo alto o bastante para os débitos do teste nunca esbarrarem no limite.
    const funded = http.post(`${API}/v1/accounts/${id}/credits`,
      JSON.stringify({ amount: '100000.00', category: 'DEPOSIT', externalReference: `load-seed-${uuid()}` }), {
        headers: { Authorization: `Bearer ${payments}`, 'Content-Type': 'application/json', 'Idempotency-Key': uuid() },
        tags: { kind: 'setup' },
      });
    if (funded.status !== 201) throw new Error(`fund ${funded.status}: ${funded.body}`);
    return id;
  };
  const accounts = [];
  for (let i = 0; i < ACCOUNTS; i++) accounts.push(open());
  return { accounts, hot: open() };
}

export function writes(data) {
  const account = pick(data.accounts);
  if (Math.random() < 0.5) {
    credit(account, 'credit', [201]);
  } else {
    debit(account, 'debit', [201]);
  }
  sleep(0.1 + Math.random() * 0.2);
}

export function reads(data) {
  const account = pick(data.accounts);
  const roll = Math.random();
  if (roll < 0.5) {
    get(`/v1/accounts/${account}/balance`, 'balance');
  } else if (roll < 0.75) {
    const at = new Date(Date.now() - 5000).toISOString();
    get(`/v1/accounts/${account}/balance?at=${encodeURIComponent(at)}`, 'balance_at');
  } else {
    get(`/v1/accounts/${account}/statement?limit=50`, 'statement');
  }
  sleep(0.05 + Math.random() * 0.1);
}

// Todos na mesma conta: as escritas se enfileiram no lock da conta, e a espera acima de 2 s vira 409 (ADR-0005).
export function hot(data) {
  if (Math.random() < 0.5) {
    credit(data.hot, 'hot_write', [201, 409]);
  } else {
    debit(data.hot, 'hot_write', [201, 409]);
  }
}

function metric(data, name) {
  return data.metrics[name] ? data.metrics[name].values : {};
}

function line(data, label, name) {
  const v = metric(data, name);
  if (v.count === undefined && v.avg === undefined) return `${label}: sem amostras`;
  const f = (x) => (x === undefined ? '-' : x.toFixed(1));
  const n = metric(data, `http_reqs{kind:${label}}`);
  return `${label}: ${n.count || 0} req (${(n.rate || 0).toFixed(1)}/s), p50 ${f(v['p(50)'])} ms, p95 ${f(v['p(95)'])} ms, p99 ${f(v['p(99)'])} ms, max ${f(v.max)} ms`;
}

export function handleSummary(data) {
  const kinds = ['credit', 'debit', 'hot_write', 'balance', 'balance_at', 'statement'];
  const reqs = metric(data, 'http_reqs');
  const thresholds = Object.entries(data.metrics)
    .filter(([, m]) => m.thresholds)
    .map(([name, m]) => `${name}: ${Object.values(m.thresholds).every((t) => t.ok) ? 'ok' : 'FALHOU'}`);
  const text = [
    `Requisições: ${reqs.count} (${(reqs.rate || 0).toFixed(1)}/s)`,
    ...kinds.map((k) => line(data, k, `http_req_duration{kind:${k}}`)),
    `409: ${metric(data, 'status_409').count || 0}, 422: ${metric(data, 'status_422').count || 0}, `
      + `503: ${metric(data, 'status_503').count || 0}, inesperados: ${metric(data, 'unexpected_status').count || 0}`,
    'Limiares:',
    ...thresholds.map((t) => `  ${t}`),
    '',
  ].join('\n');
  return { stdout: text, [`${RESULTS}/summary.json`]: JSON.stringify(data, null, 2) };
}
