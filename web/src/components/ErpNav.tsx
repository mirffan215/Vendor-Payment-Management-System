import { useTranslation } from 'react-i18next'
import { NavLink } from 'react-router-dom'

export function ErpNav() {
  const { t } = useTranslation()
  return (
    <nav className="subnav" aria-label={t('erp.title')}>
      <NavLink to="/erp" end>{t('erp.nav.admin')}</NavLink>
      <NavLink to="/erp/sync">{t('erp.nav.sync')}</NavLink>
      <NavLink to="/erp/reconciliation">{t('erp.nav.reconciliation')}</NavLink>
    </nav>
  )
}
