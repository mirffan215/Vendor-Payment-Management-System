import { useTranslation } from 'react-i18next'
import { ApiError } from '../api/client'
import { useProblemText } from '../hooks/useProblemText'

/** Where the person goes to prove again that it is them, and come back to the page they were on. */
export const stepUpHref = (path: string) => `/auth/step-up?returnUrl=${encodeURIComponent(path)}`

export function ProblemAlert({ error }: { error: unknown }) {
  const text = useProblemText()
  const { t } = useTranslation()
  if (!error) return null
  const needsStepUp = error instanceof ApiError && error.problem.errorCode === 'step_up_required'
  return (
    <p role="alert" className="alert">
      {text(error)}
      {needsStepUp && (
        <>
          {' '}
          <a href={stepUpHref(window.location.pathname + window.location.search)}>{t('stepUp.confirm')}</a>
        </>
      )}
    </p>
  )
}
