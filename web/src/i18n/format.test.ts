import { describe, expect, it } from 'vitest'
import { formatDate, formatDateTime, formatMoney, formatNumber } from './format'

const date = '2026-10-21T08:30:00Z'

describe('date formatting', () => {
  it('formats Gregorian dates', () => {
    expect(formatDate(date, 'en', 'gregorian')).toContain('2026')
    expect(formatDate(date, 'ar', 'gregorian')).toContain('2026')
  })

  it('formats Hijri (Umm al-Qura) dates for the same instant', () => {
    const hijri = formatDate(date, 'en', 'hijri')
    expect(hijri).toContain('1448')
    expect(hijri).not.toContain('2026')
    expect(formatDate(date, 'ar', 'hijri')).toContain('1448')
  })

  it('always uses Latin digits so figures are unambiguous', () => {
    expect(formatDateTime(date, 'ar', 'hijri')).toMatch(/\d/)
    expect(formatDateTime(date, 'ar', 'hijri')).not.toMatch(/[٠-٩]/)
    expect(formatNumber(1234567, 'ar')).not.toMatch(/[٠-٩]/)
  })

  it('uses UTC so the same instant renders the same day for every viewer', () => {
    expect(formatDate('2026-10-21T23:30:00Z', 'en', 'gregorian')).toContain('21')
  })
})

describe('money formatting', () => {
  it('formats SAR and other currencies with the ISO currency', () => {
    expect(formatMoney(1234.5, 'SAR', 'en')).toContain('1,234.50')
    expect(formatMoney(1234.5, 'SAR', 'ar')).toMatch(/SAR|ر\.س/)
    expect(formatMoney(99, 'USD', 'en')).toContain('99.00')
  })
})
