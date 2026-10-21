import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { createVendor, listVendors } from '../../api/staff'
import { useAction } from '../../components/Busy'
import { ProblemAlert } from '../../components/ProblemText'
import { useLoad } from '../../hooks/useLoad'
import { formatDate, type CalendarPreference } from '../../i18n/format'

const STATUSES = ['Invited', 'Submitted', 'UnderReview', 'Active', 'Rejected', 'Suspended']

export function VendorsPage({ calendar }: { calendar: CalendarPreference }) {
  const { t, i18n } = useTranslation()
  const [status, setStatus] = useState('')
  const [search, setSearch] = useState('')
  const [applied, setApplied] = useState({ status: '', search: '' })
  const list = useLoad(() => listVendors(applied.status, applied.search), `${applied.status}|${applied.search}`)
  const create = useAction()
  const [form, setForm] = useState({ legalName: '', countryCode: 'SA', taxId: '' })

  async function submit(e: FormEvent) {
    e.preventDefault()
    const created = await create.run(() => createVendor({ legalName: form.legalName, countryCode: form.countryCode.toUpperCase(), taxId: form.taxId || undefined }))
    if (created) {
      setForm({ legalName: '', countryCode: form.countryCode, taxId: '' })
      list.reload()
    }
  }

  return (
    <section aria-labelledby="vendors-title" className="card">
      <h1 id="vendors-title">{t('vendors.title')}</h1>
      <form
        className="row"
        onSubmit={(e) => {
          e.preventDefault()
          setApplied({ status, search })
        }}
      >
        <label>
          {t('vendors.search')}
          <input value={search} onChange={(e) => setSearch(e.target.value)} />
        </label>
        <label>
          {t('vendors.status')}
          <select value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">{t('vendors.allStatuses')}</option>
            {STATUSES.map((s) => (
              <option key={s} value={s}>
                {t(`status.${s}`)}
              </option>
            ))}
          </select>
        </label>
        <button type="submit">{t('vendors.filter')}</button>
      </form>

      <ProblemAlert error={list.error} />
      {list.loading && <p role="status">{t('common.loading')}</p>}
      {list.data && list.data.length === 0 && <p>{t('vendors.empty')}</p>}
      {list.data && list.data.length > 0 && (
        <div className="table-wrap" tabIndex={0}>
          <table>
            <thead>
              <tr>
                <th scope="col">{t('vendors.columns.name')}</th>
                <th scope="col">{t('vendors.columns.country')}</th>
                <th scope="col">{t('vendors.columns.status')}</th>
                <th scope="col">{t('vendors.columns.created')}</th>
              </tr>
            </thead>
            <tbody>
              {list.data.map((v) => (
                <tr key={v.id}>
                  <td>
                    <Link to={`/vendors/${v.id}`}>{v.legalName}</Link>
                  </td>
                  <td dir="ltr">{v.countryCode}</td>
                  <td>{t(`status.${v.status}`, { defaultValue: v.status })}</td>
                  <td>{formatDate(v.createdAt, i18n.language, calendar)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <h2>{t('vendors.newTitle')}</h2>
      <form onSubmit={submit} className="stack">
        <label>
          {t('vendors.legalName')}
          <input value={form.legalName} onChange={(e) => setForm({ ...form, legalName: e.target.value })} required maxLength={300} />
        </label>
        <label>
          {t('vendors.country')}
          <input value={form.countryCode} onChange={(e) => setForm({ ...form, countryCode: e.target.value })} required maxLength={2} dir="ltr" />
        </label>
        <label>
          {t('vendors.taxId')}
          <input value={form.taxId} onChange={(e) => setForm({ ...form, taxId: e.target.value })} dir="ltr" />
        </label>
        <ProblemAlert error={create.error} />
        <button type="submit" disabled={create.busy}>
          {t('vendors.create')}
        </button>
      </form>
    </section>
  )
}
