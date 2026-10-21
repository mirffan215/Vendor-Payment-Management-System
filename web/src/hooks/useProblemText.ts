import { useTranslation } from 'react-i18next'
import { ApiError } from '../api/client'

/** Turns an API failure into a translated message using the stable errorCode, never the server's English text. */
export function useProblemText() {
  const { t } = useTranslation()
  return (error: unknown): string => {
    if (error instanceof ApiError) {
      const code = error.problem.errorCode
      if (code) {
        const text = t(`errors.codes.${code}`, { defaultValue: '' })
        if (text) return text
      }
      if (error.status === 403) return t('errors.forbidden')
      if (error.status === 401) return t('errors.unauthenticated')
    }
    return t('errors.generic')
  }
}

