import { useTranslation } from 'react-i18next'
import { SUPPORTED_LOCALES, setLocale, type Locale } from '../i18n'
import type { CalendarPreference } from '../i18n/format'

interface Props {
  calendar: CalendarPreference
  onCalendarChange: (calendar: CalendarPreference) => void
  /** Called after either choice changes, so a signed-in person's choice is kept on the server too. */
  onSave?: (locale: Locale, calendar: CalendarPreference) => void
}

/** Language and calendar switches; both are real <select>s so they work with keyboards and screen readers. */
export function Preferences({ calendar, onCalendarChange, onSave }: Props) {
  const { t, i18n } = useTranslation()
  return (
    <div className="preferences">
      <label>
        <span>{t('language.label')}</span>
        <select
          value={i18n.language}
          onChange={(e) => void setLocale(i18n, e.target.value as Locale).then(() => onSave?.(e.target.value as Locale, calendar))}
          data-testid="language-select"
        >
          {SUPPORTED_LOCALES.map((l) => (
            <option key={l} value={l} lang={l}>
              {t(`language.${l}`)}
            </option>
          ))}
        </select>
      </label>
      <label>
        <span>{t('calendar.label')}</span>
        <select
          value={calendar}
          onChange={(e) => {
            onCalendarChange(e.target.value as CalendarPreference)
            onSave?.(i18n.language as Locale, e.target.value as CalendarPreference)
          }}
          data-testid="calendar-select"
        >
          <option value="gregorian">{t('calendar.gregorian')}</option>
          <option value="hijri">{t('calendar.hijri')}</option>
        </select>
      </label>
    </div>
  )
}
