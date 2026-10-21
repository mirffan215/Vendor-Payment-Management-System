import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import {
  decideRuleChange, getEntityRules, getRuleCatalog, listRuleChanges, proposeRules, type RuleChange, type RuleDefinition, type RuleSetting,
} from '../../api/invoices'
import { listEntities } from '../../api/staff'
import { useAction } from '../../components/Busy'
import { RuleName } from '../../components/Findings'
import { ProblemAlert } from '../../components/ProblemText'
import { useLoad } from '../../hooks/useLoad'

export function VerificationAdminPage() {
  const { t } = useTranslation()
  const entities = useLoad(listEntities, 'entities')
  const catalog = useLoad(getRuleCatalog, 'catalog')
  const [entityId, setEntityId] = useState('')
  const current = entityId || entities.data?.[0]?.id || ''

  return (
    <section className="card" aria-labelledby="ver-title">
      <h1 id="ver-title">{t('verificationAdmin.title')}</h1>
      <p>{t('verificationAdmin.intro')}</p>
      <ProblemAlert error={entities.error ?? catalog.error} />
      <label>
        {t('invoices.entity')}
        <select value={current} onChange={(e) => setEntityId(e.target.value)}>
          {(entities.data ?? []).map((x) => (<option key={x.id} value={x.id}>{x.code} — {x.legalName}</option>))}
        </select>
      </label>
      {current && catalog.data && <RulesEditor key={current} entityId={current} catalog={catalog.data} />}
      <ChangesInbox />
    </section>
  )
}

function RulesEditor({ entityId, catalog }: { entityId: string; catalog: RuleDefinition[] }) {
  const { t } = useTranslation()
  const rules = useLoad(() => getEntityRules(entityId), entityId)
  const act = useAction()
  const [draft, setDraft] = useState<Record<string, RuleSetting>>()
  const [note, setNote] = useState('')
  const [result, setResult] = useState<string>()

  if (rules.loading && !rules.data) return <p role="status">{t('common.loading')}</p>
  if (!rules.data) return <ProblemAlert error={rules.error ?? new Error('load')} />
  const active = rules.data.active.config.rules
  const values = draft ?? active
  const patch = (code: string, change: Partial<RuleSetting>) => setDraft({ ...values, [code]: { ...values[code], ...change } })

  async function submit(e: FormEvent) {
    e.preventDefault()
    const saved = await act.run(() => proposeRules(entityId, values, note))
    if (saved) {
      setResult(saved.status === 'Pending' ? 'pending' : 'active')
      setDraft(undefined)
      setNote('')
      rules.reload()
    }
  }

  return (
    <form onSubmit={submit} className="stack">
      <p>{t('verificationAdmin.version', { version: rules.data.active.versionNo })}</p>
      {rules.data.pending && <p className="alert">{t('verificationAdmin.pendingExists')}</p>}
      {result === 'pending' && <p role="status">{t('verificationAdmin.savedPending')}</p>}
      {result === 'active' && <p role="status">{t('verificationAdmin.savedActive')}</p>}
      {catalog.map((def) => {
        const s = values[def.code]
        return (
          <fieldset key={def.code} className="card inner">
            <legend><RuleName code={def.code} /></legend>
            <p className="muted">{t(`rules.${def.code}.help`, { defaultValue: '' })}</p>
            <div className="row">
              <label>
                <input type="checkbox" checked={s.enabled} disabled={!def.canDisable} onChange={(e) => patch(def.code, { enabled: e.target.checked })} />
                {t('verificationAdmin.enabled')}
              </label>
              <label>
                {t('verificationAdmin.severity')}
                <select value={s.severity} disabled={!def.canFlag} onChange={(e) => patch(def.code, { severity: e.target.value })}>
                  <option value="Block">{t('severity.Block')}</option>
                  <option value="Flag">{t('severity.Flag')}</option>
                </select>
              </label>
              {def.parameters.map((p) => (
                <label key={p.name}>
                  {t(`ruleParams.${p.name}`, { defaultValue: p.name })}
                  <input
                    type="number" step="any" min={p.min} max={p.max} dir="ltr" value={s.parameters[p.name]}
                    onChange={(e) => patch(def.code, { parameters: { ...s.parameters, [p.name]: Number(e.target.value) } })}
                  />
                </label>
              ))}
            </div>
          </fieldset>
        )
      })}
      <p className="muted">{t('verificationAdmin.looseningNote')}</p>
      <label>{t('verificationAdmin.note')}<input value={note} onChange={(e) => setNote(e.target.value)} required maxLength={500} /></label>
      <ProblemAlert error={act.error} />
      <button type="submit" disabled={act.busy || !draft}>{t('verificationAdmin.propose')}</button>
    </form>
  )
}

function ChangesInbox() {
  const { t } = useTranslation()
  const changes = useLoad(() => listRuleChanges('Pending'), 'pending')
  return (
    <div className="stack">
      <h2>{t('verificationAdmin.inboxTitle')}</h2>
      <p>{t('verificationAdmin.inboxIntro')}</p>
      {changes.error && <ProblemAlert error={changes.error} />}
      {changes.data?.length === 0 && <p>{t('verificationAdmin.inboxEmpty')}</p>}
      <ul className="plain">
        {(changes.data ?? []).map((c) => (<ChangeCard key={c.change.id} change={c} onDone={changes.reload} />))}
      </ul>
    </div>
  )
}

function ChangeCard({ change, onDone }: { change: RuleChange; onDone: () => void }) {
  const { t } = useTranslation()
  const act = useAction()
  const [comment, setComment] = useState('')
  const id = change.change.id!
  const decide = async (d: 'approve' | 'reject') => {
    if ((await act.run(() => decideRuleChange(id, d, comment))) !== undefined) onDone()
  }
  return (
    <li className="card inner">
      <p>{change.change.note}</p>
      <p>{t('verificationAdmin.loosens')}</p>
      <ul className="plain">
        {change.change.loosenedRules.map((code) => (
          <li key={code}><RuleName code={code} />: {describe(change.base.config.rules[code], change.change.config.rules[code], t)}</li>
        ))}
      </ul>
      <label>{t('bank.comment')}<input value={comment} onChange={(e) => setComment(e.target.value)} maxLength={500} /></label>
      <ProblemAlert error={act.error} />
      <div className="row">
        <button type="button" disabled={act.busy} onClick={() => void decide('approve')}>{t('bank.approve')}</button>
        <button type="button" disabled={act.busy || !comment.trim()} onClick={() => void decide('reject')}>{t('bank.reject')}</button>
      </div>
    </li>
  )
}

function describe(from: RuleSetting, to: RuleSetting, t: (k: string, o?: Record<string, unknown>) => string): string {
  const parts: string[] = []
  if (from.enabled && !to.enabled) parts.push(t('verificationAdmin.switchedOff'))
  if (from.severity !== to.severity) parts.push(`${t(`severity.${from.severity}`)} → ${t(`severity.${to.severity}`)}`)
  for (const [name, value] of Object.entries(to.parameters)) {
    if (from.parameters[name] !== value) parts.push(`${t(`ruleParams.${name}`, { defaultValue: name })}: ${from.parameters[name]} → ${value}`)
  }
  return parts.join('; ')
}
