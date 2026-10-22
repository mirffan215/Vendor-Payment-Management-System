import { useTranslation } from 'react-i18next'
import type { Finding } from '../api/invoices'

/** A verification finding in the user's language. The key is stable; the evidence fills in the details. */
export function FindingText({ finding }: { finding: Finding }) {
  const { t } = useTranslation()
  return <span>{t(`findings.${finding.key}`, { ...finding.evidence, defaultValue: finding.key })}</span>
}

export function RuleName({ code }: { code: string }) {
  const { t } = useTranslation()
  return <>{t(`rules.${code}.title`, { defaultValue: code })}</>
}
