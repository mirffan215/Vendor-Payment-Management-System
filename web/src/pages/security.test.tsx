import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { I18nextProvider } from 'react-i18next'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../api/client'
import { ProblemAlert, stepUpHref } from '../components/ProblemText'
import { createI18n } from '../i18n'
import { json, mockFetch, renderApp, staffSession } from '../test/helpers'

beforeEach(() => {
  document.documentElement.dir = 'ltr'
})
afterEach(() => vi.unstubAllGlobals())

const stepUpError = () => new ApiError({ status: 403, errorCode: 'step_up_required', title: 'x' })

describe('step-up prompt', () => {
  it('asks the person to confirm it is them and links to the step-up sign-in that returns to this page', () => {
    window.history.pushState({}, '', '/payments/proposals/p1?tab=release')
    render(<I18nextProvider i18n={createI18n('en')}><ProblemAlert error={stepUpError()} /></I18nextProvider>)
    expect(screen.getByRole('alert')).toHaveTextContent('please confirm it is you')
    expect(screen.getByRole('link', { name: 'Confirm it is me' })).toHaveAttribute('href', stepUpHref('/payments/proposals/p1?tab=release'))
    expect(stepUpHref('/a?b=c&d')).toBe('/auth/step-up?returnUrl=%2Fa%3Fb%3Dc%26d')
  })

  it('says it in Arabic too', () => {
    render(<I18nextProvider i18n={createI18n('ar')}><ProblemAlert error={stepUpError()} /></I18nextProvider>)
    expect(screen.getByRole('alert')).toHaveTextContent('تأكيد هويتك')
    expect(screen.getByRole('link', { name: 'تأكيد هويتي' })).toBeInTheDocument()
  })

  it('does not offer step-up for other errors', () => {
    render(<I18nextProvider i18n={createI18n('en')}><ProblemAlert error={new ApiError({ status: 403, errorCode: 'forbidden', title: 'x' })} /></I18nextProvider>)
    expect(screen.queryByRole('link')).not.toBeInTheDocument()
  })
})

describe('sign out everywhere', () => {
  it('ends all sessions and returns to the sign-in page', async () => {
    const fetchMock = mockFetch({
      '/api/v1/session/revoke-all': () => new Response(null, { status: 204 }),
      'POST /api/v1/session/revoke-all': () => new Response(null, { status: 204 }),
      '/api/v1/session': () => json(staffSession),
      '/api/v1/notifications/unread-count': () => json({ count: 0 }),
    })
    renderApp('en', '/')
    await userEvent.click(await screen.findByRole('button', { name: 'Sign out everywhere' }))
    await waitFor(() => expect(fetchMock.mock.calls.some(([u, i]) => String(u).endsWith('/session/revoke-all') && (i as RequestInit).method === 'POST')).toBe(true))
    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeInTheDocument()
  })
})
