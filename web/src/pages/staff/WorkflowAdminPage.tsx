import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import {
  decideMaterialityChange, decideWorkflowChange, getEntityWorkflow, getMateriality, getMaterialityFields, listMaterialityChanges, listWorkflowChanges,
  previewMatrix, proposeMateriality, proposeWorkflow, simulateWorkflow, validateWorkflow,
  type DefinitionIssue, type MaterialityConfig, type Simulation, type StepSpec, type WorkflowSpec,
} from '../../api/approvals'
import { listEntities } from '../../api/staff'
import { useAction } from '../../components/Busy'
import { ProblemAlert } from '../../components/ProblemText'
import { useLoad } from '../../hooks/useLoad'

const TEMPLATE: WorkflowSpec = {
  name: 'Two levels',
  start: 'l1',
  steps: [
    { id: 'l1', type: 'Approval', next: 'l2', approvers: [{ kind: 'Role', value: 'Invoice Approver' }], mode: 'AnyOne', dueHours: 48, reminderHours: 24 },
    { id: 'l2', type: 'Approval', approvers: [{ kind: 'Role', value: 'Invoice Approver' }], mode: 'AnyOne' },
  ],
}

export function WorkflowAdminPage() {
  const { t } = useTranslation()
  const entities = useLoad(listEntities, 'entities')
  const [entityId, setEntityId] = useState('')
  const current = entityId || entities.data?.[0]?.id || ''

  return (
    <div className="stack">
      <section className="card" aria-labelledby="wf-title">
        <h1 id="wf-title">{t('workflowAdmin.title')}</h1>
        <p>{t('workflowAdmin.intro')}</p>
        <ProblemAlert error={entities.error} />
        <label>
          {t('invoices.entity')}
          <select value={current} onChange={(e) => setEntityId(e.target.value)}>
            {(entities.data ?? []).map((x) => (<option key={x.id} value={x.id}>{x.code} — {x.legalName}</option>))}
          </select>
        </label>
        {current && <WorkflowEditor key={current} entityId={current} />}
      </section>
      <WorkflowChangesInbox />
      {current && <MaterialitySection key={`m-${current}`} entityId={current} />}
      <MaterialityChangesInbox />
    </div>
  )
}

export function StepSummary({ step }: { step: StepSpec }) {
  const { t } = useTranslation()
  return (
    <li>
      <strong dir="ltr">{step.id}</strong> — {t(`workflowAdmin.stepType.${step.type}`, { defaultValue: step.type })}
      {step.type === 'Approval' && (
        <>
          : {(step.approvers ?? []).map((a) => a.value).join(', ')} ({t(`approvals.mode.${step.mode ?? 'AnyOne'}`, { defaultValue: step.mode })})
          {step.dueHours ? <> · {t('workflowAdmin.dueHours', { hours: step.dueHours })}</> : null}
          {step.next ? <> → <span dir="ltr">{step.next}</span></> : null}
        </>
      )}
      {step.type === 'Parallel' && <>: {(step.branches ?? []).join(', ')} ({step.join})</>}
      {step.type === 'Condition' && <>: {t('workflowAdmin.cases', { count: step.cases?.length ?? 0 })}</>}
    </li>
  )
}

function WorkflowEditor({ entityId }: { entityId: string }) {
  const { t } = useTranslation()
  const state = useLoad(() => getEntityWorkflow(entityId), entityId)
  const act = useAction()
  const [text, setText] = useState<string>()
  const [note, setNote] = useState('')
  const [issues, setIssues] = useState<DefinitionIssue[]>()
  const [sim, setSim] = useState<Simulation>()
  const [amount, setAmount] = useState('1000')
  const [currency, setCurrency] = useState('SAR')
  const [saved, setSaved] = useState(false)

  if (state.loading && !state.data) return <p role="status">{t('common.loading')}</p>
  if (!state.data) return <ProblemAlert error={state.error ?? new Error('load')} />
  const active = state.data.active
  const editorText = text ?? JSON.stringify(active?.spec ?? TEMPLATE, null, 2)

  const parse = (): WorkflowSpec | undefined => {
    try {
      return JSON.parse(editorText) as WorkflowSpec
    } catch {
      setIssues([{ code: 'json', message: '', stepId: null }])
      return undefined
    }
  }

  async function validate() {
    const spec = parse()
    if (spec) setIssues(await act.run(() => validateWorkflow(entityId, spec)))
  }

  async function simulate() {
    const spec = parse()
    if (spec) setSim(await act.run(() => simulateWorkflow(entityId, { spec, amount: Number(amount), currency, vendorId: null, source: null, dimensions: null, maker: null })))
  }

  async function propose(e: FormEvent, fromMatrix: boolean) {
    e.preventDefault()
    const spec = fromMatrix ? null : parse()
    if (!fromMatrix && !spec) return
    if ((await act.run(() => proposeWorkflow(entityId, spec ?? null, fromMatrix, note))) !== undefined) {
      setSaved(true)
      setNote('')
      setText(undefined)
      state.reload()
    }
  }

  async function showMatrix() {
    const spec = await act.run(() => previewMatrix(entityId))
    if (spec) setText(JSON.stringify(spec, null, 2))
  }

  return (
    <form onSubmit={(e) => void propose(e, false)} className="stack">
      <h2>{t('workflowAdmin.active')}</h2>
      {!active && <p>{t('workflowAdmin.none')}</p>}
      {active && (
        <>
          <p>{t('workflowAdmin.version', { version: active.versionNo, source: t(`workflowAdmin.source.${active.source}`, { defaultValue: active.source }) })}</p>
          <ol className="plain">{(active.spec?.steps ?? []).map((s) => (<StepSummary key={s.id} step={s} />))}</ol>
        </>
      )}
      {state.data.pending && <p className="alert">{t('workflowAdmin.pendingExists')}</p>}
      {saved && <p role="status">{t('workflowAdmin.savedPending')}</p>}

      <h2>{t('workflowAdmin.editor')}</h2>
      <p className="muted">{t('workflowAdmin.editorHelp')}</p>
      <label>
        {t('workflowAdmin.definition')}
        <textarea dir="ltr" rows={14} value={editorText} onChange={(e) => { setText(e.target.value); setIssues(undefined) }} spellCheck={false} />
      </label>
      <div className="row">
        <button type="button" className="secondary" disabled={act.busy} onClick={() => void validate()}>{t('workflowAdmin.validate')}</button>
        <button type="button" className="secondary" disabled={act.busy} onClick={() => void showMatrix()}>{t('workflowAdmin.loadMatrix')}</button>
      </div>
      {issues && issues.length === 0 && <p role="status">{t('workflowAdmin.valid')}</p>}
      {issues && issues.length > 0 && (
        <ul className="plain alert">
          {issues.map((i, n) => (<li key={n}>{i.code === 'json' ? t('workflowAdmin.notJson') : t(`workflowAdmin.issue.${i.code}`, { defaultValue: i.message })}{i.stepId && <> ({i.stepId})</>}</li>))}
        </ul>
      )}

      <h3>{t('workflowAdmin.simulate')}</h3>
      <div className="row">
        <label>{t('invoices.total')}<input type="number" dir="ltr" step="any" value={amount} onChange={(e) => setAmount(e.target.value)} /></label>
        <label>{t('invoices.currency')}<input dir="ltr" value={currency} maxLength={3} onChange={(e) => setCurrency(e.target.value.toUpperCase())} /></label>
        <button type="button" className="secondary" disabled={act.busy} onClick={() => void simulate()}>{t('workflowAdmin.runSimulation')}</button>
      </div>
      {sim && (
        <div role="status">
          <p>{t(`workflowAdmin.outcome.${sim.outcome}`, { defaultValue: sim.outcome })}</p>
          <ol className="plain">{sim.route.map((r) => (<li key={r.stepId}><span dir="ltr">{r.stepId}</span> — {t('workflowAdmin.approverCount', { count: r.approvers.length })}</li>))}</ol>
        </div>
      )}

      <p className="muted">{t('workflowAdmin.twoPerson')}</p>
      <label>{t('workflowAdmin.note')}<input value={note} onChange={(e) => setNote(e.target.value)} maxLength={500} /></label>
      <ProblemAlert error={act.error} />
      <div className="row">
        <button type="submit" disabled={act.busy || !note.trim()}>{t('workflowAdmin.propose')}</button>
        <button type="button" className="secondary" disabled={act.busy || !note.trim()} onClick={(e) => void propose(e, true)}>{t('workflowAdmin.proposeMatrix')}</button>
      </div>
    </form>
  )
}

function WorkflowChangesInbox() {
  const { t } = useTranslation()
  const changes = useLoad(() => listWorkflowChanges('Pending'), 'wf-pending')
  const act = useAction()
  const [comment, setComment] = useState('')
  return (
    <section className="card" aria-labelledby="wfc-title">
      <h2 id="wfc-title">{t('workflowAdmin.inboxTitle')}</h2>
      <p>{t('workflowAdmin.inboxIntro')}</p>
      <ProblemAlert error={changes.error ?? act.error} />
      {changes.data?.length === 0 && <p>{t('workflowAdmin.inboxEmpty')}</p>}
      <ul className="plain">
        {(changes.data ?? []).map((c) => (
          <li key={c.change.id} className="card inner">
            <p>{c.change.note}</p>
            <ol className="plain">{(c.change.spec?.steps ?? []).map((s) => (<StepSummary key={s.id} step={s} />))}</ol>
            {c.base && <p className="muted">{t('workflowAdmin.replaces', { version: c.base.versionNo })}</p>}
            <label>{t('bank.comment')}<input value={comment} onChange={(e) => setComment(e.target.value)} maxLength={500} /></label>
            <div className="row">
              <button type="button" disabled={act.busy} onClick={() => void act.run(() => decideWorkflowChange(c.change.id!, 'approve', comment)).then((r) => r && changes.reload())}>{t('bank.approve')}</button>
              <button type="button" className="secondary" disabled={act.busy || !comment.trim()} onClick={() => void act.run(() => decideWorkflowChange(c.change.id!, 'reject', comment)).then((r) => r && changes.reload())}>{t('bank.reject')}</button>
            </div>
          </li>
        ))}
      </ul>
    </section>
  )
}

function MaterialitySection({ entityId }: { entityId: string }) {
  const { t } = useTranslation()
  const state = useLoad(() => getMateriality(entityId), entityId)
  const fields = useLoad(getMaterialityFields, 'fields')
  const act = useAction()
  const [draft, setDraft] = useState<MaterialityConfig>()
  const [note, setNote] = useState('')
  const [saved, setSaved] = useState(false)

  if (!state.data || !fields.data) return <section className="card"><ProblemAlert error={state.error ?? fields.error} /></section>
  const value = draft ?? state.data.active.config
  const all = [...fields.data.money, ...fields.data.other]
  const toggle = (f: string) => setDraft({ ...value, materialFields: value.materialFields.includes(f) ? value.materialFields.filter((x) => x !== f) : [...value.materialFields, f] })

  async function submit(e: FormEvent) {
    e.preventDefault()
    if ((await act.run(() => proposeMateriality(entityId, value, note))) !== undefined) {
      setSaved(true)
      setDraft(undefined)
      setNote('')
      state.reload()
    }
  }

  return (
    <section className="card" aria-labelledby="mat-title">
      <h2 id="mat-title">{t('materiality.title')}</h2>
      <p>{t('materiality.intro')}</p>
      {state.data.pending && <p className="alert">{t('materiality.pendingExists')}</p>}
      {saved && <p role="status">{t('materiality.savedPending')}</p>}
      <form onSubmit={submit} className="stack">
        <div className="row">
          <label>{t('materiality.percent')}<input type="number" min="0" max="100" step="any" dir="ltr" value={value.amountPercent} onChange={(e) => setDraft({ ...value, amountPercent: Number(e.target.value) })} /></label>
          <label>{t('materiality.absolute')}<input type="number" min="0" step="any" dir="ltr" value={value.amountAbsolute} onChange={(e) => setDraft({ ...value, amountAbsolute: Number(e.target.value) })} /></label>
        </div>
        <fieldset>
          <legend>{t('materiality.fields')}</legend>
          {all.map((f) => (
            <label key={f} className="inline">
              <input type="checkbox" checked={value.materialFields.includes(f)} onChange={() => toggle(f)} /> {t(`materiality.field.${f}`, { defaultValue: f })}
            </label>
          ))}
        </fieldset>
        <label>{t('workflowAdmin.note')}<input value={note} onChange={(e) => setNote(e.target.value)} maxLength={500} /></label>
        <ProblemAlert error={act.error} />
        <button type="submit" disabled={act.busy || !draft || !note.trim()}>{t('materiality.propose')}</button>
      </form>
    </section>
  )
}

function MaterialityChangesInbox() {
  const { t } = useTranslation()
  const changes = useLoad(() => listMaterialityChanges('Pending'), 'mat-pending')
  const act = useAction()
  const [comment, setComment] = useState('')
  if (changes.data?.length === 0) return null
  return (
    <section className="card" aria-labelledby="matc-title">
      <h2 id="matc-title">{t('materiality.inboxTitle')}</h2>
      <ProblemAlert error={changes.error ?? act.error} />
      <ul className="plain">
        {(changes.data ?? []).map((c) => (
          <li key={c.change.id} className="card inner">
            <p>{c.change.note}</p>
            <p>{t('materiality.summary', { percent: c.change.config.amountPercent, absolute: c.change.config.amountAbsolute, count: c.change.config.materialFields.length })}</p>
            <p className="muted">{t('materiality.was', { percent: c.base.config.amountPercent, absolute: c.base.config.amountAbsolute, count: c.base.config.materialFields.length })}</p>
            <label>{t('bank.comment')}<input value={comment} onChange={(e) => setComment(e.target.value)} maxLength={500} /></label>
            <div className="row">
              <button type="button" disabled={act.busy} onClick={() => void act.run(() => decideMaterialityChange(c.change.id!, 'approve', comment)).then((r) => r && changes.reload())}>{t('bank.approve')}</button>
              <button type="button" className="secondary" disabled={act.busy || !comment.trim()} onClick={() => void act.run(() => decideMaterialityChange(c.change.id!, 'reject', comment)).then((r) => r && changes.reload())}>{t('bank.reject')}</button>
            </div>
          </li>
        ))}
      </ul>
    </section>
  )
}
