import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { I18nextProvider } from 'react-i18next'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import App from './App'
import { createI18n } from './i18n'

const session = {
  userId: 'u1',
  tenantId: 't1',
  email: 'alice@acme.com',
  displayName: 'Alice',
  locale: 'en',
  csrfToken: 'csrf-token',
}

function problem(status: number) {
  return new Response(JSON.stringify({ status, errorCode: 'x' }), {
    status,
    headers: { 'Content-Type': 'application/problem+json' },
  })
}

function json(body: unknown) {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } })
}

function mockFetch(routes: Record<string, () => Response>) {
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === 'string' ? input : input.toString()
    const key = Object.keys(routes).find((k) => url.startsWith(k.split(' ').pop()!) && (k.includes(' ') ? k.startsWith(init?.method ?? 'GET') : true))
    if (!key) throw new Error(`Unexpected request ${init?.method ?? 'GET'} ${url}`)
    return routes[key]()
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

function renderApp(locale: 'en' | 'ar' = 'en', path = '/') {
  window.history.pushState({}, '', path)
  return render(
    <I18nextProvider i18n={createI18n(locale)}>
      <App />
    </I18nextProvider>,
  )
}

beforeEach(() => {
  document.documentElement.dir = 'ltr'
})

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('anonymous visitor', () => {
  it('is sent to the sign-in page and can look up providers', async () => {
    const fetchMock = mockFetch({
      '/api/v1/session': () => problem(401),
      '/auth/providers': () => json([{ id: 'p1', displayName: 'Acme SSO', kind: 3 }]),
    })
    renderApp()
    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeInTheDocument()

    await userEvent.type(screen.getByLabelText('Organisation identifier'), 'acme')
    await userEvent.click(screen.getByRole('button', { name: 'Find sign-in options' }))

    const link = await screen.findByRole('link', { name: 'Continue with Acme SSO' })
    expect(link).toHaveAttribute('href', '/auth/login?provider=p1&returnUrl=%2F')
    expect(fetchMock).toHaveBeenCalledWith('/auth/providers?tenant=acme', expect.anything())
  })

  it('shows a clear message when sign-in was denied', async () => {
    mockFetch({ '/api/v1/session': () => problem(401) })
    renderApp('en', '/login?denied=1')
    expect(await screen.findByRole('alert')).toHaveTextContent('Sign-in was not permitted')
  })
})

describe('right-to-left support', () => {
  it('renders Arabic with dir="rtl" and lang="ar", and switches back to LTR', async () => {
    mockFetch({ '/api/v1/session': () => problem(401) })
    renderApp('ar')
    expect(await screen.findByRole('heading', { name: 'تسجيل الدخول' })).toBeInTheDocument()
    expect(document.documentElement).toHaveAttribute('dir', 'rtl')
    expect(document.documentElement).toHaveAttribute('lang', 'ar')

    await userEvent.selectOptions(screen.getByTestId('language-select'), 'en')
    await waitFor(() => expect(document.documentElement).toHaveAttribute('dir', 'ltr'))
    expect(screen.getByRole('heading', { name: 'Sign in' })).toBeInTheDocument()
  })

  it('remembers the chosen language', async () => {
    mockFetch({ '/api/v1/session': () => problem(401) })
    renderApp('en')
    await screen.findByRole('heading', { name: 'Sign in' })
    await userEvent.selectOptions(screen.getByTestId('language-select'), 'ar')
    await waitFor(() => expect(window.localStorage.getItem('vpms.locale')).toBe('ar'))
  })
})

describe('signed-in user', () => {
  it('sees their details and can sign out with the antiforgery header', async () => {
    const fetchMock = mockFetch({
      '/api/v1/session': () => json(session),
      'POST /auth/logout': () => new Response(null, { status: 204 }),
    })
    renderApp()
    expect(await screen.findByRole('heading', { name: 'Welcome, Alice' })).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Sign out' }))
    await screen.findByRole('heading', { name: 'Sign in' })

    const logout = fetchMock.mock.calls.find(([url]) => String(url) === '/auth/logout')!
    expect(new Headers((logout[1] as RequestInit).headers).get('X-XSRF-TOKEN')).toBe('csrf-token')
  })

  it('lists audit events with Hijri dates when the Hijri calendar is selected', async () => {
    mockFetch({
      '/api/v1/session': () => json(session),
      '/api/v1/audit/events': () =>
        json({
          items: [
            { sequence: 1, id: 'e1', occurredAt: '2026-10-21T08:30:00Z', actorType: 'User', action: 'auth.login.succeeded', entityType: 'User', hash: 'h' },
          ],
          nextAfterSequence: null,
        }),
    })
    renderApp('en', '/audit')
    const table = await screen.findByRole('table')
    expect(within(table).getByText('auth.login.succeeded')).toBeInTheDocument()
    expect(within(table).getByText(/2026/)).toBeInTheDocument()

    await userEvent.selectOptions(screen.getByTestId('calendar-select'), 'hijri')
    expect(within(table).getByText(/1448/)).toBeInTheDocument()
  })

  it('tells users without permission that they cannot view the audit trail', async () => {
    mockFetch({ '/api/v1/session': () => json(session), '/api/v1/audit/events': () => problem(403) })
    renderApp('ar', '/audit')
    expect(await screen.findByRole('alert')).toHaveTextContent('ليست لديك صلاحية عرض سجل التدقيق.')
  })
})
