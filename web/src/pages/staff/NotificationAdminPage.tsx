import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import {
  NOTIFICATION_KINDS, approveChannelChange, cancelChannelChange, getChannel, getDeadLetters, getDeliverySummary, getTemplates, listChannelChanges, previewTemplate, proposeChannel,
  rejectChannelChange, resetTemplate, saveTemplate, type ChannelConfigItem, type TemplateItem,
} from '../../api/notifications'
import { useAction } from '../../components/Busy'
import { ProblemAlert } from '../../components/ProblemText'
import { useLoad } from '../../hooks/useLoad'
import { formatDateTime, type CalendarPreference } from '../../i18n/format'

export function NotificationAdminPage({ calendar }: { calendar: CalendarPreference }) {
  const { t } = useTranslation()
  return (
    <div className="stack erp">
      <section className="card" aria-labelledby="nadmin-title">
        <h1 id="nadmin-title">{t('notificationAdmin.title')}</h1>
        <p>{t('notificationAdmin.intro')}</p>
        <p className="muted">{t('notificationAdmin.secretNote')}</p>
      </section>
      <Deliveries calendar={calendar} />
      <ChannelCard channel="Email" />
      <ChannelCard channel="Webhook" />
      <ChannelCard channel="Push" />
      <ChangesInbox calendar={calendar} />
      <Templates />
    </div>
  )
}

function Deliveries({ calendar }: { calendar: CalendarPreference }) {
  const { t, i18n } = useTranslation()
  const summary = useLoad(getDeliverySummary, 'delivery-summary')
  const dead = useLoad(getDeadLetters, 'dead-letters')
  return (
    <section className="card" aria-labelledby="deliveries-title">
      <h2 id="deliveries-title">{t('notificationAdmin.deliveries')}</h2>
      <ProblemAlert error={summary.error ?? dead.error} />
      {summary.data && (
        <div className="tiles">
          {(['pending', 'sent', 'skipped', 'deadLetter'] as const).map((k) => (<div key={k}><strong>{summary.data![k]}</strong>{t(`notificationAdmin.delivery.${k}`)}</div>))}
        </div>
      )}
      {dead.data?.length === 0 && <p>{t('notificationAdmin.noDeadLetters')}</p>}
      <ul className="plain">
        {(dead.data ?? []).map((d) => (
          <li key={d.id} className="row">
            <strong>{t(`notifications.kind.${d.kind}`, { defaultValue: d.kind })}</strong>
            <span>{t(`notifications.channel.${d.channel}`, { defaultValue: d.channel })}</span>
            <span dir="ltr">{d.subjectLabel}</span>
            <span className="alert">{d.lastError}</span>
            <span className="muted">{formatDateTime(d.createdAt, i18n.language, calendar)}</span>
          </li>
        ))}
      </ul>
    </section>
  )
}

const PLACEHOLDERS = '{{subject}} {{detail}} {{count}} {{url}}'

const str = (c: Record<string, unknown> | undefined, k: string, d = '') => (typeof c?.[k] === 'string' ? (c[k] as string) : d)

function ChannelCard({ channel }: { channel: 'Email' | 'Webhook' | 'Push' }) {
  const { t } = useTranslation()
  const state = useLoad(() => getChannel(channel), `channel-${channel}`)
  const act = useAction()
  const base = state.data?.active?.config
  const [note, setNote] = useState('')
  const [f, setF] = useState<Record<string, string | boolean | string[]>>({})
  const val = (k: string, d = '') => (typeof f[k] === 'string' ? (f[k] as string) : str(base, k, d))
  const set = (k: string, v: string | boolean | string[]) => setF({ ...f, [k]: v })
  const provider = val('provider', channel === 'Email' ? 'Smtp' : 'Logging')
  const kinds = Array.isArray(f.kinds) ? (f.kinds as string[]) : Array.isArray(base?.kinds) ? (base!.kinds as string[]) : []

  const build = (): Record<string, unknown> => {
    if (channel === 'Push') return { provider: 'Logging' }
    if (channel === 'Webhook') return { url: val('url'), kinds }
    return provider === 'Graph'
      ? { provider, fromAddress: val('fromAddress'), fromName: val('fromName') || null, graphTenantId: val('graphTenantId'), graphClientId: val('graphClientId'), secretRef: val('secretRef'), mailbox: val('mailbox'), smtpPort: 0, smtpStartTls: false }
      : {
          provider, fromAddress: val('fromAddress'), fromName: val('fromName') || null, smtpHost: val('smtpHost'), smtpPort: Number(val('smtpPort', '587')),
          smtpStartTls: typeof f.smtpStartTls === 'boolean' ? f.smtpStartTls : base?.smtpStartTls !== false, smtpUsername: val('smtpUsername') || null, secretRef: val('secretRef') || null,
        }
  }

  return (
    <section className="card" aria-labelledby={`ch-${channel}`}>
      <h2 id={`ch-${channel}`}>{t(`notifications.channel.${channel}`)}</h2>
      <ProblemAlert error={state.error ?? act.error} />
      {state.data && !state.data.active && <p>{t('notificationAdmin.notConfigured')}</p>}
      {state.data?.active && <p role="status">{t('notificationAdmin.inForce', { version: state.data.active.versionNo })}</p>}
      {state.data?.pending && <p role="status">{t('notificationAdmin.waiting', { version: state.data.pending.versionNo })}</p>}
      <form className="stack" onSubmit={(e) => { e.preventDefault(); void act.run(() => proposeChannel(channel, build(), note)).then((r) => { if (r) { setNote(''); state.reload() } }) }}>
        {channel === 'Email' && (
          <>
            <label>{t('notificationAdmin.provider')}
              <select value={provider} onChange={(e) => set('provider', e.target.value)}>
                <option value="Smtp">{t('notificationAdmin.smtp')}</option>
                <option value="Graph">{t('notificationAdmin.graph')}</option>
              </select>
            </label>
            <div className="row">
              <label>{t('notificationAdmin.fromAddress')}<input dir="ltr" value={val('fromAddress')} onChange={(e) => set('fromAddress', e.target.value)} /></label>
              <label>{t('notificationAdmin.fromName')}<input value={val('fromName')} onChange={(e) => set('fromName', e.target.value)} /></label>
            </div>
            {provider === 'Graph' ? (
              <div className="row">
                <label>{t('notificationAdmin.graphTenant')}<input dir="ltr" value={val('graphTenantId')} onChange={(e) => set('graphTenantId', e.target.value)} /></label>
                <label>{t('notificationAdmin.graphClient')}<input dir="ltr" value={val('graphClientId')} onChange={(e) => set('graphClientId', e.target.value)} /></label>
                <label>{t('notificationAdmin.mailbox')}<input dir="ltr" value={val('mailbox')} onChange={(e) => set('mailbox', e.target.value)} /></label>
                <label>{t('notificationAdmin.secretRef')}<input dir="ltr" autoComplete="off" value={val('secretRef')} onChange={(e) => set('secretRef', e.target.value)} /></label>
              </div>
            ) : (
              <div className="row">
                <label>{t('notificationAdmin.smtpHost')}<input dir="ltr" value={val('smtpHost')} onChange={(e) => set('smtpHost', e.target.value)} /></label>
                <label>{t('notificationAdmin.smtpPort')}<input type="number" value={val('smtpPort', '587')} onChange={(e) => set('smtpPort', e.target.value)} /></label>
                <label>{t('notificationAdmin.smtpUser')}<input dir="ltr" autoComplete="off" value={val('smtpUsername')} onChange={(e) => set('smtpUsername', e.target.value)} /></label>
                <label>{t('notificationAdmin.secretRef')}<input dir="ltr" autoComplete="off" value={val('secretRef')} onChange={(e) => set('secretRef', e.target.value)} /></label>
                <label className="inline">
                  <input type="checkbox" checked={typeof f.smtpStartTls === 'boolean' ? f.smtpStartTls : base?.smtpStartTls !== false} onChange={(e) => set('smtpStartTls', e.target.checked)} />
                  {t('notificationAdmin.startTls')}
                </label>
              </div>
            )}
          </>
        )}
        {channel === 'Webhook' && (
          <>
            <label>{t('notificationAdmin.webhookUrl')}<input dir="ltr" type="url" value={val('url')} onChange={(e) => set('url', e.target.value)} /></label>
            <fieldset>
              <legend>{t('notificationAdmin.webhookKinds')}</legend>
              <div className="row">
                {NOTIFICATION_KINDS.map((k) => (
                  <label key={k} className="inline">
                    <input type="checkbox" checked={kinds.includes(k)} onChange={(e) => set('kinds', e.target.checked ? [...kinds, k] : kinds.filter((x) => x !== k))} />
                    {t(`notifications.kind.${k}`)}
                  </label>
                ))}
              </div>
            </fieldset>
          </>
        )}
        {channel === 'Push' && <p className="muted">{t('notificationAdmin.pushNote')}</p>}
        <label>{t('notificationAdmin.note')}<input value={note} maxLength={500} onChange={(e) => setNote(e.target.value)} /></label>
        <button type="submit" disabled={act.busy || !note.trim() || !!state.data?.pending}>{t('notificationAdmin.propose')}</button>
      </form>
    </section>
  )
}

function ChangesInbox({ calendar }: { calendar: CalendarPreference }) {
  const { t, i18n } = useTranslation()
  const changes = useLoad(listChannelChanges, 'channel-changes')
  const act = useAction()
  const [comments, setComments] = useState<Record<string, string>>({})
  const decide = async (c: ChannelConfigItem, action: 'approve' | 'reject') => {
    const r = await act.run(() => (action === 'approve' ? approveChannelChange(c.id, comments[c.id] ?? '') : rejectChannelChange(c.id, comments[c.id] ?? '')))
    if (r) changes.reload()
  }
  return (
    <section className="card" aria-labelledby="chg-title">
      <h2 id="chg-title">{t('notificationAdmin.changes')}</h2>
      <ProblemAlert error={act.error} />
      {changes.data?.length === 0 && <p>{t('notificationAdmin.noChanges')}</p>}
      <ul className="plain">
        {(changes.data ?? []).map((c) => (
          <li key={c.id} className="card inner">
            <strong>{t(`notifications.channel.${c.channel}`, { defaultValue: c.channel })}</strong> — {t('notificationAdmin.version', { version: c.versionNo })}
            <p>{c.note}</p>
            <p className="muted">{formatDateTime(c.createdAt, i18n.language, calendar)}</p>
            <pre dir="ltr" className="config">{JSON.stringify(c.config, null, 2)}</pre>
            <label>{t('erp.comment')}<input value={comments[c.id] ?? ''} maxLength={500} onChange={(e) => setComments({ ...comments, [c.id]: e.target.value })} /></label>
            <div className="row">
              <button type="button" disabled={act.busy} onClick={() => void decide(c, 'approve')}>{t('erp.changes.approve')}</button>
              <button type="button" className="secondary" disabled={act.busy || !(comments[c.id] ?? '').trim()} onClick={() => void decide(c, 'reject')}>{t('erp.changes.reject')}</button>
              <button type="button" className="secondary" disabled={act.busy} onClick={() => void act.run(() => cancelChannelChange(c.id)).then((r) => r && changes.reload())}>{t('erp.changes.cancel')}</button>
            </div>
          </li>
        ))}
      </ul>
    </section>
  )
}

function Templates() {
  const { t } = useTranslation()
  const templates = useLoad(getTemplates, 'templates')
  const act = useAction()
  const [kind, setKind] = useState<string>(NOTIFICATION_KINDS[0])
  const [channel, setChannel] = useState<'InApp' | 'Email' | 'Webhook'>('Email')
  const [locale, setLocale] = useState<'en' | 'ar'>('en')
  const [draft, setDraft] = useState<{ key: string; subject: string; body: string }>()
  const [preview, setPreview] = useState<{ subject: string; body: string }>()

  const key = `${kind}|${channel}|${locale}`
  const stored: TemplateItem | undefined = templates.data?.find((x) => x.kind === kind && x.channel === channel && x.locale === locale)
  const subject = draft?.key === key ? draft.subject : stored?.subject ?? ''
  const body = draft?.key === key ? draft.body : stored?.body ?? ''
  const edit = (s: string, b: string) => { setDraft({ key, subject: s, body: b }); setPreview(undefined) }

  return (
    <section className="card" aria-labelledby="tpl-title">
      <h2 id="tpl-title">{t('notificationAdmin.templates')}</h2>
      <p>{t('notificationAdmin.templatesIntro')}</p>
      <p className="muted" dir="ltr">{PLACEHOLDERS}</p>
      <ProblemAlert error={templates.error ?? act.error} />
      <div className="row">
        <label>{t('notificationAdmin.kind')}
          <select value={kind} onChange={(e) => setKind(e.target.value)}>
            {NOTIFICATION_KINDS.map((k) => (<option key={k} value={k}>{t(`notifications.kind.${k}`)}</option>))}
          </select>
        </label>
        <label>{t('notificationAdmin.channel')}
          <select value={channel} onChange={(e) => setChannel(e.target.value as typeof channel)}>
            {(['InApp', 'Email', 'Webhook'] as const).map((c) => (<option key={c} value={c}>{t(`notifications.channel.${c}`)}</option>))}
          </select>
        </label>
        <label>{t('notificationAdmin.language')}
          <select value={locale} onChange={(e) => setLocale(e.target.value as typeof locale)}>
            <option value="en">{t('language.en')}</option>
            <option value="ar">{t('language.ar')}</option>
          </select>
        </label>
      </div>
      {stored?.isOverride && <p role="status">{t('notificationAdmin.customised')}</p>}
      <label>{t('notificationAdmin.subject')}<input value={subject} maxLength={150} onChange={(e) => edit(e.target.value, body)} dir={locale === 'ar' ? 'rtl' : 'ltr'} /></label>
      <label>{t('notificationAdmin.body')}<textarea className="map" value={body} maxLength={2000} onChange={(e) => edit(subject, e.target.value)} dir={locale === 'ar' ? 'rtl' : 'ltr'} /></label>
      <div className="row">
        <button type="button" className="secondary" disabled={act.busy} onClick={() => void act.run(() => previewTemplate({ kind, channel, locale, subject, body })).then((p) => p && setPreview(p))}>{t('notificationAdmin.preview')}</button>
        <button type="button" disabled={act.busy} onClick={() => void act.run(() => saveTemplate({ kind, channel, locale, subject, body })).then((r) => { if (r) { setDraft(undefined); templates.reload() } })}>{t('notificationAdmin.save')}</button>
        <button type="button" className="secondary" disabled={act.busy || !stored?.isOverride} onClick={() => void act.run(() => resetTemplate(kind, channel, locale)).then(() => { setDraft(undefined); templates.reload() })}>{t('notificationAdmin.reset')}</button>
      </div>
      {preview && (
        <div className="card inner" role="status" dir={locale === 'ar' ? 'rtl' : 'ltr'}>
          <strong>{preview.subject}</strong>
          <p>{preview.body}</p>
        </div>
      )}
    </section>
  )
}
