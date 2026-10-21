import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import {
  assignRole, createEntity, listAuthorities, listEntities, listProviders, listRoles, listUsers, revokeAuthority,
  setEntityActive, setProviderEnabled, setUserActive,
} from '../../api/staff'
import { useAction } from '../../components/Busy'
import { ProblemAlert } from '../../components/ProblemText'
import { useLoad } from '../../hooks/useLoad'
import { formatDate, formatMoney, type CalendarPreference } from '../../i18n/format'

const TABS = ['entities', 'users', 'roles', 'providers', 'authorities'] as const
type Tab = (typeof TABS)[number]

export function AdminPage({ calendar }: { calendar: CalendarPreference }) {
  const { t } = useTranslation()
  const [tab, setTab] = useState<Tab>('entities')
  return (
    <section className="card" aria-labelledby="admin-title">
      <h1 id="admin-title">{t('admin.title')}</h1>
      <div role="tablist" aria-label={t('admin.title')} className="row">
        {TABS.map((id) => (
          <button key={id} role="tab" type="button" aria-selected={tab === id} className={tab === id ? 'active' : 'secondary'} onClick={() => setTab(id)}>
            {t(`admin.tabs.${id}`)}
          </button>
        ))}
      </div>
      <div role="tabpanel">
        {tab === 'entities' && <Entities />}
        {tab === 'users' && <Users />}
        {tab === 'roles' && <Roles />}
        {tab === 'providers' && <Providers />}
        {tab === 'authorities' && <Authorities calendar={calendar} />}
      </div>
    </section>
  )
}

function Entities() {
  const { t } = useTranslation()
  const list = useLoad(listEntities, 'entities')
  const act = useAction()
  const [f, setF] = useState({ code: '', legalName: '', countryCode: 'SA', baseCurrency: 'SAR' })

  async function submit(e: FormEvent) {
    e.preventDefault()
    if (await act.run(() => createEntity(f))) {
      setF({ ...f, code: '', legalName: '' })
      list.reload()
    }
  }

  return (
    <>
      <ProblemAlert error={list.error} />
      <ul className="plain">
        {(list.data ?? []).map((e) => (
          <li key={e.id} className="row">
            <strong dir="ltr">{e.code}</strong>
            <span>{e.legalName}</span>
            <span dir="ltr">{e.baseCurrency}</span>
            <span>{t(`status.${e.status}`, { defaultValue: e.status })}</span>
            <button type="button" disabled={act.busy} onClick={() => void act.run(() => setEntityActive(e.id, e.status !== 'Active')).then(list.reload)}>
              {e.status === 'Active' ? t('admin.deactivate') : t('admin.activate')}
            </button>
          </li>
        ))}
      </ul>
      <form onSubmit={submit} className="stack">
        <h2>{t('admin.newEntity')}</h2>
        <label>{t('admin.code')}<input value={f.code} onChange={(e) => setF({ ...f, code: e.target.value })} required dir="ltr" /></label>
        <label>{t('admin.legalName')}<input value={f.legalName} onChange={(e) => setF({ ...f, legalName: e.target.value })} required /></label>
        <label>{t('admin.country')}<input value={f.countryCode} onChange={(e) => setF({ ...f, countryCode: e.target.value })} required maxLength={2} dir="ltr" /></label>
        <label>{t('admin.currency')}<input value={f.baseCurrency} onChange={(e) => setF({ ...f, baseCurrency: e.target.value })} required maxLength={3} dir="ltr" /></label>
        <ProblemAlert error={act.error} />
        <button type="submit" disabled={act.busy}>{t('admin.create')}</button>
      </form>
    </>
  )
}

function Users() {
  const { t } = useTranslation()
  const users = useLoad(listUsers, 'users')
  const roles = useLoad(listRoles, 'roles')
  const act = useAction()
  const [pick, setPick] = useState<Record<string, string>>({})
  return (
    <>
      <ProblemAlert error={users.error} />
      <ProblemAlert error={act.error} />
      <div className="table-wrap" tabIndex={0}>
        <table>
          <thead>
            <tr>
              <th scope="col">{t('admin.email')}</th>
              <th scope="col">{t('admin.roles')}</th>
              <th scope="col">{t('vendors.status')}</th>
              <th scope="col">{t('admin.actions')}</th>
            </tr>
          </thead>
          <tbody>
            {(users.data ?? []).map((u) => (
              <tr key={u.id}>
                <td dir="ltr">{u.email}</td>
                <td>{u.roles.join(', ') || '—'}</td>
                <td>{t(`status.${u.status}`, { defaultValue: u.status })}</td>
                <td className="row">
                  <button type="button" disabled={act.busy} onClick={() => void act.run(() => setUserActive(u.id, u.status !== 'Active')).then(users.reload)}>
                    {u.status === 'Active' ? t('admin.disable') : t('admin.enable')}
                  </button>
                  <select aria-label={t('admin.roles')} value={pick[u.id] ?? ''} onChange={(e) => setPick({ ...pick, [u.id]: e.target.value })}>
                    <option value="">{t('admin.chooseRole')}</option>
                    {(roles.data ?? []).map((r) => (
                      <option key={r.id} value={r.id}>{r.name}</option>
                    ))}
                  </select>
                  <button type="button" disabled={act.busy || !pick[u.id]} onClick={() => void act.run(() => assignRole(u.id, pick[u.id])).then(users.reload)}>
                    {t('admin.assign')}
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  )
}

function Roles() {
  const { t } = useTranslation()
  const roles = useLoad(listRoles, 'roles')
  return (
    <>
      <p>{t('admin.sodNote')}</p>
      <ProblemAlert error={roles.error} />
      <ul className="plain">
        {(roles.data ?? []).map((r) => (
          <li key={r.id}>
            <strong>{r.name}</strong> {r.isSystem && <em>{t('admin.system')}</em>} · {t('admin.assignments', { count: r.assignmentCount })}
            <div dir="ltr" className="muted">{r.permissions.join(', ')}</div>
          </li>
        ))}
      </ul>
    </>
  )
}

function Providers() {
  const { t } = useTranslation()
  const list = useLoad(listProviders, 'providers')
  const act = useAction()
  const kinds: Record<number, string> = { 1: 'EntraId', 2: 'Google', 3: 'GenericOidc' }
  return (
    <>
      <ProblemAlert error={list.error} />
      <ProblemAlert error={act.error} />
      <ul className="plain">
        {(list.data ?? []).map((p) => (
          <li key={p.id} className="row">
            <strong>{p.displayName}</strong>
            <span>{t(`admin.kind.${kinds[p.kind] ?? 'GenericOidc'}`)}</span>
            <span dir="ltr">{p.authority}</span>
            <span>{p.enabled ? t('admin.enabled') : t('admin.disabled')}</span>
            <button type="button" disabled={act.busy} onClick={() => void act.run(() => setProviderEnabled(p.id, !p.enabled)).then(list.reload)}>
              {p.enabled ? t('admin.disable') : t('admin.enable')}
            </button>
          </li>
        ))}
      </ul>
    </>
  )
}

function Authorities({ calendar }: { calendar: CalendarPreference }) {
  const { t, i18n } = useTranslation()
  const list = useLoad(listAuthorities, 'authorities')
  const act = useAction()
  return (
    <>
      <p>{t('admin.authorityNote')}</p>
      <ProblemAlert error={list.error} />
      <ProblemAlert error={act.error} />
      <ul className="plain">
        {(list.data ?? []).map((a) => (
          <li key={a.id} className="row">
            <span dir="ltr">{a.userId}</span>
            <span>{formatMoney(a.maxAmount, a.currencyCode, i18n.language)}</span>
            <span>{formatDate(a.validFrom, i18n.language, calendar)}</span>
            <span>{t(`status.${a.status}`, { defaultValue: a.status })}</span>
            <button type="button" disabled={act.busy} onClick={() => void act.run(() => revokeAuthority(a.id)).then(list.reload)}>
              {t('admin.revoke')}
            </button>
          </li>
        ))}
      </ul>
    </>
  )
}
