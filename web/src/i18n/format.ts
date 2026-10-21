export type CalendarPreference = 'gregorian' | 'hijri'

/** Intl locale tag including the calendar and Latin digits so numbers stay unambiguous in financial screens. */
function tag(locale: string, calendar: CalendarPreference): string {
  const base = locale.startsWith('ar') ? 'ar-SA' : 'en-GB'
  const cal = calendar === 'hijri' ? 'islamic-umalqura' : 'gregory'
  return `${base}-u-ca-${cal}-nu-latn`
}

export function formatDate(value: string | number | Date, locale: string, calendar: CalendarPreference): string {
  return new Intl.DateTimeFormat(tag(locale, calendar), { dateStyle: 'medium', timeZone: 'UTC' }).format(new Date(value))
}

export function formatDateTime(value: string | number | Date, locale: string, calendar: CalendarPreference): string {
  return new Intl.DateTimeFormat(tag(locale, calendar), {
    dateStyle: 'medium',
    timeStyle: 'short',
    timeZone: 'UTC',
  }).format(new Date(value))
}

export function formatNumber(value: number, locale: string): string {
  return new Intl.NumberFormat(tag(locale, 'gregorian')).format(value)
}

/** Money is always shown with its ISO currency code context, e.g. SAR, never a guessed default. */
export function formatMoney(amount: number, currency: string, locale: string): string {
  return new Intl.NumberFormat(tag(locale, 'gregorian'), { style: 'currency', currency }).format(amount)
}
