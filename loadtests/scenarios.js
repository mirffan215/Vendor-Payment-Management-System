// k6 scenarios for the VPMS API. Run against the load-test host (see loadtests/README.md):
//   k6 run -e PROFILE=smoke  loadtests/scenarios.js
//   k6 run -e PROFILE=load   loadtests/scenarios.js
import http from 'k6/http'
import { sleep } from 'k6'
import { BASE, iso, login, ok, pick, post } from './lib.js'

const PROFILES = {
  // One of each, for a quick check that everything answers.
  smoke: {
    browse: { executor: 'constant-vus', vus: 1, duration: '20s', exec: 'browse' },
    writes: { executor: 'constant-vus', vus: 1, duration: '20s', exec: 'writes' },
    exports: { executor: 'constant-vus', vus: 1, duration: '20s', exec: 'exportReports' },
  },
  // Steady day-to-day use plus a burst of writes on one tenant (the audit chain serialises them).
  load: {
    browse: { executor: 'ramping-vus', startVUs: 0, stages: [{ duration: '30s', target: 20 }, { duration: '2m', target: 20 }, { duration: '15s', target: 0 }], exec: 'browse' },
    writes: { executor: 'ramping-vus', startVUs: 0, stages: [{ duration: '30s', target: 10 }, { duration: '2m', target: 10 }, { duration: '15s', target: 0 }], exec: 'writes' },
    exports: { executor: 'constant-vus', vus: 2, duration: '2m45s', exec: 'exportReports' },
  },
  // Twice the load, to find where it bends.
  stress: {
    browse: { executor: 'ramping-vus', startVUs: 0, stages: [{ duration: '30s', target: 60 }, { duration: '2m', target: 60 }, { duration: '15s', target: 0 }], exec: 'browse' },
    writes: { executor: 'ramping-vus', startVUs: 0, stages: [{ duration: '30s', target: 30 }, { duration: '2m', target: 30 }, { duration: '15s', target: 0 }], exec: 'writes' },
    exports: { executor: 'constant-vus', vus: 4, duration: '2m45s', exec: 'exportReports' },
  },
}

export const options = {
  insecureSkipTLSVerify: true, // the load-test host uses a self-signed certificate
  noCookiesReset: true, // each virtual user signs in once and keeps its session across iterations
  scenarios: PROFILES[__ENV.PROFILE || 'smoke'],
  thresholds: {
    http_req_failed: ['rate<0.01'],
    'http_req_duration{kind:list}': ['p(95)<600'],
    'http_req_duration{kind:audit}': ['p(95)<800'],
    'http_req_duration{kind:report}': ['p(95)<2000'],
    'http_req_duration{kind:write}': ['p(95)<1500'],
    'http_req_duration{kind:export}': ['p(95)<8000'],
    checks: ['rate>0.99'],
  },
}

let clerkCsrf, auditorCsrf
let sequence = 0

export function setup() {
  const health = http.get(`${BASE}/health/ready`)
  if (health.status !== 200) throw new Error(`The host is not ready: ${health.status}`)
  login('clerk')
  const vendors = http.get(`${BASE}/api/v1/vendors?take=50`).json()
  login('admin')
  const entities = http.get(`${BASE}/api/v1/admin/legal-entities`).json()
  return { vendorIds: (vendors.items || vendors).map((v) => v.id), entityId: __ENV.ENTITY_ID || entities[0].id }
}

export function browse() {
  // one person per virtual user: a second sign-in would replace the first session cookie
  clerkCsrf = clerkCsrf || login('clerk')
  const roll = Math.random()
  if (roll < 0.30) {
    ok(http.get(`${BASE}/api/v1/invoices?take=50&skip=${Math.floor(Math.random() * 40) * 50}`, { tags: { kind: 'list', name: 'invoices-page' } }), 'invoices page')
  } else if (roll < 0.45) {
    ok(http.get(`${BASE}/api/v1/invoices?take=50&status=Verified`, { tags: { kind: 'list', name: 'invoices-status' } }), 'invoices by status')
  } else if (roll < 0.55) {
    ok(http.get(`${BASE}/api/v1/invoices?take=50&search=LOAD-00${Math.floor(Math.random() * 90) + 10}`, { tags: { kind: 'list', name: 'invoices-search' } }), 'invoices search')
  } else if (roll < 0.65) {
    ok(http.get(`${BASE}/api/v1/vendors?take=50`, { tags: { kind: 'list', name: 'vendors' } }), 'vendors')
  } else if (roll < 0.80) {
    ok(http.get(`${BASE}/api/v1/audit/events?limit=50`, { tags: { kind: 'audit', name: 'audit-events' } }), 'audit events')
  } else if (roll < 0.90) {
    ok(http.get(`${BASE}/api/v1/reports/payables-aging?asOf=${iso(0)}`, { tags: { kind: 'report', name: 'report-aging' } }), 'aging report')
  } else {
    ok(http.get(`${BASE}/api/v1/notifications/unread-count`, { tags: { kind: 'list', name: 'unread-count' } }), 'unread count')
  }
  sleep(0.2 + Math.random() * 0.5)
}

export function writes(data) {
  clerkCsrf = clerkCsrf || login('clerk')
  const net = 100 + Math.floor(Math.random() * 50) * 10
  const tax = Math.round(net * 0.15 * 100) / 100
  const number = `K6-${__VU}-${__ITER}-${sequence++}`
  const res = post('/api/v1/invoices', {
    legalEntityId: data.entityId,
    vendorId: pick(data.vendorIds),
    data: {
      invoiceNumber: number, invoiceDate: iso(5), dueDate: iso(-25), currencyCode: 'SAR', subtotal: net, taxTotal: tax, total: net + tax, poReference: null, supplierTaxInvoiceRef: null,
      lines: [{ description: 'Load test', quantity: 1, unitPrice: net, lineAmount: net, taxCode: 'S', taxRate: 0.15, taxAmount: tax, poLineRef: null, dimensionsJson: null }],
    },
  }, clerkCsrf, { kind: 'write', name: 'invoice-create' })
  ok(res, 'invoice create', 201)
  sleep(0.1 + Math.random() * 0.3)
}

export function exportReports() {
  auditorCsrf = auditorCsrf || login('auditor')
  const format = pick(['xlsx', 'pdf', 'csv'])
  ok(http.get(`${BASE}/api/v1/reports/payables-aging/export?format=${format}&asOf=${iso(0)}`, { tags: { kind: 'export', name: `export-${format}` } }), `export ${format}`)
  sleep(2)
}
