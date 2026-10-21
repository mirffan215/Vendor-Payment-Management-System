import { render } from '@testing-library/react'
import { I18nextProvider } from 'react-i18next'
import { vi } from 'vitest'
import App from '../App'
import { createI18n } from '../i18n'

export function problem(status: number, errorCode = 'x') {
  return new Response(JSON.stringify({ status, errorCode }), { status, headers: { 'Content-Type': 'application/problem+json' } })
}

export function json(body: unknown) {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } })
}

/** Routes are matched by "METHOD /path-prefix" (method optional, GET assumed) in the order given. */
export function mockFetch(routes: Record<string, (init?: RequestInit) => Response>) {
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === 'string' ? input : input.toString()
    const method = (init?.method ?? 'GET').toUpperCase()
    const key = Object.keys(routes).find((k) => {
      const [m, p] = k.includes(' ') ? k.split(' ') : ['GET', k]
      return m === method && url.startsWith(p)
    })
    if (!key) throw new Error(`Unexpected request ${method} ${url}`)
    return routes[key](init)
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

export function renderApp(locale: 'en' | 'ar' = 'en', path = '/') {
  window.history.pushState({}, '', path)
  return render(
    <I18nextProvider i18n={createI18n(locale)}>
      <App />
    </I18nextProvider>,
  )
}

export const staffSession = {
  userId: 'u1', tenantId: 't1', email: 'alice@acme.com', displayName: 'Alice', locale: 'en', csrfToken: 'csrf-staff',
}

export const portalSession = {
  userId: 'pu1', vendorId: 'v1', vendorName: 'Al Amal Trading', email: 'ap@amal.example', displayName: 'AP Clerk', csrfToken: 'csrf-portal',
}

export function sentBody(fetchMock: ReturnType<typeof vi.fn>, method: string, urlPart: string): unknown {
  const call = fetchMock.mock.calls.find(([u, i]) => String(u).includes(urlPart) && ((i as RequestInit | undefined)?.method ?? 'GET') === method)
  return call ? JSON.parse(String((call[1] as RequestInit).body)) : undefined
}

export function sentHeader(fetchMock: ReturnType<typeof vi.fn>, method: string, urlPart: string, header: string): string | null {
  const call = fetchMock.mock.calls.find(([u, i]) => String(u).includes(urlPart) && ((i as RequestInit | undefined)?.method ?? 'GET') === method)
  return call ? new Headers((call[1] as RequestInit).headers).get(header) : null
}
