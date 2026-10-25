import http from 'k6/http'
import { check } from 'k6'

export const BASE = __ENV.BASE_URL || 'https://localhost:5443'

/** Signs the persona in through the load-test door of the test host and returns the anti-forgery token. Each VU keeps its own cookie jar. */
export function login(persona) {
  const door = http.get(`${BASE}/_load/login/${persona}`)
  check(door, { 'signed in': (r) => r.status === 204 })
  const session = http.get(`${BASE}/api/v1/session`)
  return session.json('csrfToken')
}

export function ok(res, name, status = 200) {
  return check(res, { [`${name}: ${status}`]: (r) => r.status === status })
}

export function post(path, body, csrf, tags) {
  return http.post(`${BASE}${path}`, JSON.stringify(body), { headers: { 'Content-Type': 'application/json', 'X-XSRF-TOKEN': csrf }, tags })
}

export const pick = (list) => list[Math.floor(Math.random() * list.length)]
export const iso = (daysAgo) => new Date(Date.now() - daysAgo * 86400000).toISOString().slice(0, 10)
