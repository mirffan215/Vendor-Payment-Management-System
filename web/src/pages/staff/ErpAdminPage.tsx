import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import {
  approveConfigChange, cancelConfigChange, ENTITY_SET_ROLES, getConnection, getMapping, listConfigChanges, mapToText, proposeConnection, proposeMapping, rejectConfigChange,
  testConnection, textToMap, type ConnectionConfig, type DimensionMap, type ErpConfig, type MappingConfig,
} from '../../api/erp'
import { listEntities } from '../../api/staff'
import { useAction } from '../../components/Busy'
import { ErpNav } from '../../components/ErpNav'
import { ProblemAlert } from '../../components/ProblemText'
import { useLoad } from '../../hooks/useLoad'
import { formatDateTime, type CalendarPreference } from '../../i18n/format'

const EMPTY_CONNECTION: ConnectionConfig = {
  provider: 'D365Finance', baseUrl: '', authorityTenantId: '', clientId: '', secretRef: '', entitySets: {}, pageSize: 200, timeoutSeconds: 60,
}

const EMPTY_MAPPING: MappingConfig = { company: '', journalName: '', postingProfile: null, paymentTermsMap: {}, taxCodeMap: {}, dimensions: [] }

export function ErpAdminPage({ calendar }: { calendar: CalendarPreference }) {
  const { t } = useTranslation()
  const entities = useLoad(listEntities, 'entities')
  const [entityId, setEntityId] = useState('')
  const current = entityId || entities.data?.[0]?.id || ''

  return (
    <div className="stack erp">
      <ErpNav />
      <section className="card" aria-labelledby="erp-title">
        <h1 id="erp-title">{t('erp.title')}</h1>
        <p>{t('erp.intro')}</p>
        <p className="muted">{t('erp.noWrites')}</p>
      </section>
      <ConnectionSection />
      <section className="card" aria-labelledby="erp-map-title">
        <h2 id="erp-map-title">{t('erp.mapping.title')}</h2>
        <ProblemAlert error={entities.error} />
        <label>
          {t('invoices.entity')}
          <select value={current} onChange={(e) => setEntityId(e.target.value)}>
            {(entities.data ?? []).map((x) => (<option key={x.id} value={x.id}>{x.code} — {x.legalName}</option>))}
          </select>
        </label>
        {current && <MappingSection key={current} entityId={current} calendar={calendar} />}
      </section>
      <ChangesInbox calendar={calendar} />
    </div>
  )
}

function StatusLine({ config, kind }: { config: ErpConfig | null; kind: 'active' | 'pending' }) {
  const { t } = useTranslation()
  if (!config) return null
  return <p role="status">{t(`erp.state.${kind}`, { version: config.versionNo })}</p>
}

function ConnectionSection() {
  const { t } = useTranslation()
  const state = useLoad(getConnection, 'connection')
  const act = useAction()
  const base = state.data?.active?.connection ?? EMPTY_CONNECTION
  return (
    <section className="card" aria-labelledby="erp-conn-title">
      <h2 id="erp-conn-title">{t('erp.connection.title')}</h2>
      <ProblemAlert error={state.error ?? act.error} />
      {state.data && !state.data.active && <p>{t('erp.connection.none')}</p>}
      <StatusLine config={state.data?.active ?? null} kind="active" />
      <StatusLine config={state.data?.pending ?? null} kind="pending" />
      {state.data && (
        <ConnectionForm
          key={`${state.data.active?.id}-${state.data.pending?.id}`}
          initial={base}
          disabled={act.busy || !!state.data.pending}
          onSubmit={async (config, note) => {
            const r = await act.run(() => proposeConnection(config, note))
            if (r) state.reload()
          }}
        />
      )}
    </section>
  )
}

function ConnectionForm({ initial, disabled, onSubmit }: { initial: ConnectionConfig; disabled: boolean; onSubmit: (c: ConnectionConfig, note: string) => Promise<void> }) {
  const { t } = useTranslation()
  const [c, setC] = useState(initial)
  const [note, setNote] = useState('')
  const set = <K extends keyof ConnectionConfig>(key: K, value: ConnectionConfig[K]) => setC({ ...c, [key]: value })
  const setSet = (role: string, value: string) => {
    const next = { ...c.entitySets }
    if (value.trim()) next[role] = value.trim()
    else delete next[role]
    setC({ ...c, entitySets: next })
  }
  return (
    <form className="stack" onSubmit={(e) => { e.preventDefault(); void onSubmit(c, note) }}>
      <label>{t('erp.connection.provider')}
        <select value={c.provider} onChange={(e) => set('provider', e.target.value)}>
          <option value="D365Finance">{t('erp.connection.d365')}</option>
        </select>
      </label>
      <label>{t('erp.connection.baseUrl')}<input dir="ltr" type="url" value={c.baseUrl} onChange={(e) => set('baseUrl', e.target.value)} /></label>
      <label>{t('erp.connection.authority')}<input dir="ltr" value={c.authorityTenantId} onChange={(e) => set('authorityTenantId', e.target.value)} /></label>
      <label>{t('erp.connection.clientId')}<input dir="ltr" value={c.clientId} onChange={(e) => set('clientId', e.target.value)} /></label>
      <label>{t('erp.connection.secretRef')}<input dir="ltr" value={c.secretRef} autoComplete="off" onChange={(e) => set('secretRef', e.target.value)} /></label>
      <p className="muted">{t('erp.connection.secretHint')}</p>
      <div className="row">
        <label>{t('erp.connection.pageSize')}<input type="number" value={c.pageSize} onChange={(e) => set('pageSize', Number(e.target.value))} /></label>
        <label>{t('erp.connection.timeout')}<input type="number" value={c.timeoutSeconds} onChange={(e) => set('timeoutSeconds', Number(e.target.value))} /></label>
      </div>
      <fieldset>
        <legend>{t('erp.connection.entitySets')}</legend>
        <div className="row">
          {ENTITY_SET_ROLES.map((role) => (
            <label key={role}>{t(`erp.connection.role.${role}`)}<input dir="ltr" value={c.entitySets[role] ?? ''} onChange={(e) => setSet(role, e.target.value)} /></label>
          ))}
        </div>
      </fieldset>
      <label>{t('erp.note')}<input value={note} maxLength={500} onChange={(e) => setNote(e.target.value)} /></label>
      <button type="submit" disabled={disabled || !note.trim()}>{t('erp.propose')}</button>
    </form>
  )
}

function MappingSection({ entityId, calendar }: { entityId: string; calendar: CalendarPreference }) {
  const { t, i18n } = useTranslation()
  const state = useLoad(() => getMapping(entityId), `mapping-${entityId}`)
  const act = useAction()
  const [test, setTest] = useState<{ ok: boolean; reason: string | null }>()
  const base = state.data?.active?.mapping ?? EMPTY_MAPPING
  const pending = state.data?.pending
  return (
    <div className="stack">
      <ProblemAlert error={state.error ?? act.error} />
      {state.data && !state.data.active && <p>{t('erp.mapping.none')}</p>}
      <StatusLine config={state.data?.active ?? null} kind="active" />
      {pending && <p role="status">{t('erp.state.pendingAt', { version: pending.versionNo, date: pending.createdAt ? formatDateTime(pending.createdAt, i18n.language, calendar) : '' })}</p>}
      {state.data && (
        <MappingForm
          key={`${state.data.active?.id}-${pending?.id}`}
          initial={base}
          disabled={act.busy || !!pending}
          onSubmit={async (config, note) => {
            const r = await act.run(() => proposeMapping(entityId, config, note))
            if (r) state.reload()
          }}
        />
      )}
      <div>
        <button type="button" className="secondary" disabled={act.busy || !state.data?.active} onClick={() => void act.run(() => testConnection(entityId)).then((r) => r && setTest({ ok: r.ok, reason: r.errorCode }))}>
          {t('erp.mapping.test')}
        </button>
        {test && (
          <p role="status" className={test.ok ? undefined : 'alert'}>
            {test.ok ? t('erp.mapping.testOk') : t('erp.mapping.testFailed', { reason: t(`erp.testError.${test.reason}`, { defaultValue: test.reason ?? '' }) })}
          </p>
        )}
      </div>
    </div>
  )
}

function MappingForm({ initial, disabled, onSubmit }: { initial: MappingConfig; disabled: boolean; onSubmit: (c: MappingConfig, note: string) => Promise<void> }) {
  const { t } = useTranslation()
  const [company, setCompany] = useState(initial.company)
  const [journal, setJournal] = useState(initial.journalName)
  const [profile, setProfile] = useState(initial.postingProfile ?? '')
  const [terms, setTerms] = useState(mapToText(initial.paymentTermsMap))
  const [taxes, setTaxes] = useState(mapToText(initial.taxCodeMap))
  const [dims, setDims] = useState(initial.dimensions.map((d) => ({ vpms: d.vpmsDimension, erp: d.erpDimension, def: d.defaultValue ?? '', values: mapToText(d.valueMap) })))
  const [note, setNote] = useState('')

  const edit = (i: number, patch: Partial<(typeof dims)[number]>) => setDims(dims.map((d, at) => (at === i ? { ...d, ...patch } : d)))
  const build = (): MappingConfig => ({
    company: company.trim(), journalName: journal.trim(), postingProfile: profile.trim() || null, paymentTermsMap: textToMap(terms), taxCodeMap: textToMap(taxes),
    dimensions: dims.map<DimensionMap>((d) => ({ vpmsDimension: d.vpms.trim(), erpDimension: d.erp.trim(), defaultValue: d.def.trim() || null, valueMap: textToMap(d.values) })),
  })

  return (
    <form className="stack" onSubmit={(e) => { e.preventDefault(); void onSubmit(build(), note) }}>
      <div className="row">
        <label>{t('erp.mapping.company')}<input dir="ltr" value={company} onChange={(e) => setCompany(e.target.value)} /></label>
        <label>{t('erp.mapping.journal')}<input dir="ltr" value={journal} onChange={(e) => setJournal(e.target.value)} /></label>
        <label>{t('erp.mapping.postingProfile')}<input dir="ltr" value={profile} onChange={(e) => setProfile(e.target.value)} /></label>
      </div>
      <label>{t('erp.mapping.paymentTerms')}<textarea className="map" dir="ltr" value={terms} onChange={(e) => setTerms(e.target.value)} /></label>
      <label>{t('erp.mapping.taxCodes')}<textarea className="map" dir="ltr" value={taxes} onChange={(e) => setTaxes(e.target.value)} /></label>
      <fieldset>
        <legend>{t('erp.mapping.dimensions')}</legend>
        {dims.map((d, i) => (
          <div key={i} className="card inner">
            <div className="row">
              <label>{t('erp.mapping.vpmsDimension')}<input dir="ltr" value={d.vpms} onChange={(e) => edit(i, { vpms: e.target.value })} /></label>
              <label>{t('erp.mapping.erpDimension')}<input dir="ltr" value={d.erp} onChange={(e) => edit(i, { erp: e.target.value })} /></label>
              <label>{t('erp.mapping.defaultValue')}<input dir="ltr" value={d.def} onChange={(e) => edit(i, { def: e.target.value })} /></label>
            </div>
            <label>{t('erp.mapping.valueMap')}<textarea className="map" dir="ltr" value={d.values} onChange={(e) => edit(i, { values: e.target.value })} /></label>
            <button type="button" className="secondary" onClick={() => setDims(dims.filter((_, at) => at !== i))}>{t('erp.mapping.removeDimension')}</button>
          </div>
        ))}
        <button type="button" className="secondary" onClick={() => setDims([...dims, { vpms: '', erp: '', def: '', values: '' }])}>{t('erp.mapping.addDimension')}</button>
      </fieldset>
      <label>{t('erp.note')}<input value={note} maxLength={500} onChange={(e) => setNote(e.target.value)} /></label>
      <button type="submit" disabled={disabled || !note.trim()}>{t('erp.propose')}</button>
    </form>
  )
}

function ChangesInbox({ calendar }: { calendar: CalendarPreference }) {
  const { t, i18n } = useTranslation()
  const changes = useLoad(() => listConfigChanges('Pending'), 'erp-changes')
  const act = useAction()
  const [comments, setComments] = useState<Record<string, string>>({})
  const decide = async (id: string, action: 'approve' | 'reject') => {
    const comment = comments[id] ?? ''
    const r = await act.run(() => (action === 'approve' ? approveConfigChange(id, comment) : rejectConfigChange(id, comment)))
    if (r) changes.reload()
  }
  const items = changes.data ?? []
  return (
    <section className="card" aria-labelledby="erp-changes-title">
      <h2 id="erp-changes-title">{t('erp.changes.title')}</h2>
      <ProblemAlert error={act.error} />
      {changes.data && items.length === 0 && <p>{t('erp.changes.empty')}</p>}
      <ul className="plain">
        {items.map(({ change, base }) => (
          <li key={change.id} className="card inner">
            <strong>{t(`erp.changes.kind.${change.kind}`)}</strong> — {t('erp.changes.version', { version: change.versionNo })}
            <p>{change.note}</p>
            <p className="muted">{change.createdAt ? formatDateTime(change.createdAt, i18n.language, calendar) : ''}</p>
            <ChangeSummary change={change} base={base} />
            <label>{t('erp.comment')}<input value={comments[change.id!] ?? ''} maxLength={500} onChange={(e) => setComments({ ...comments, [change.id!]: e.target.value })} /></label>
            <div className="row">
              <button type="button" disabled={act.busy} onClick={() => void decide(change.id!, 'approve')}>{t('erp.changes.approve')}</button>
              <button type="button" className="secondary" disabled={act.busy || !(comments[change.id!] ?? '').trim()} onClick={() => void decide(change.id!, 'reject')}>{t('erp.changes.reject')}</button>
              <button type="button" className="secondary" disabled={act.busy} onClick={() => void act.run(() => cancelConfigChange(change.id!)).then((r) => r && changes.reload())}>{t('erp.changes.cancel')}</button>
            </div>
          </li>
        ))}
      </ul>
    </section>
  )
}

/** Shows only the fields that differ, so a reviewer sees exactly what the change would alter. */
function ChangeSummary({ change, base }: { change: ErpConfig; base: ErpConfig | null }) {
  const { t } = useTranslation()
  const flatten = (c: ErpConfig | null): Record<string, string> => {
    const out: Record<string, string> = {}
    const add = (prefix: string, value: unknown) => {
      if (value && typeof value === 'object' && !Array.isArray(value)) Object.entries(value).forEach(([k, v]) => add(`${prefix}${prefix ? '.' : ''}${k}`, v))
      else out[prefix] = JSON.stringify(value ?? null)
    }
    add('', c?.connection ?? c?.mapping ?? {})
    return out
  }
  const after = flatten(change)
  const before = flatten(base)
  const keys = [...new Set([...Object.keys(before), ...Object.keys(after)])].filter((k) => before[k] !== after[k])
  if (keys.length === 0) return null
  return (
    <table>
      <thead><tr><th>{t('erp.changes.field')}</th><th>{t('erp.changes.current')}</th><th>{t('erp.changes.proposed')}</th></tr></thead>
      <tbody>
        {keys.map((k) => (<tr key={k}><td dir="ltr">{k}</td><td dir="ltr">{before[k] ?? '—'}</td><td dir="ltr">{after[k] ?? '—'}</td></tr>))}
      </tbody>
    </table>
  )
}
