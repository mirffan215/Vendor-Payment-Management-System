import i18next, { type i18n as I18n } from 'i18next'
import { initReactI18next } from 'react-i18next'
import ar from './locales/ar.json'
import en from './locales/en.json'

export const SUPPORTED_LOCALES = ['en', 'ar'] as const
export type Locale = (typeof SUPPORTED_LOCALES)[number]

const STORAGE_KEY = 'vpms.locale'

export function isRtl(locale: string): boolean {
  return locale.startsWith('ar')
}

export function initialLocale(): Locale {
  try {
    const stored = window.localStorage.getItem(STORAGE_KEY)
    if (stored === 'en' || stored === 'ar') return stored
  } catch {
    // storage can be unavailable (private mode, blocked); fall back to the browser language
  }
  return window.navigator.language.toLowerCase().startsWith('ar') ? 'ar' : 'en'
}

/** Keeps <html lang> and <html dir> in step with the active language so the whole layout mirrors for Arabic. */
export function applyDocumentLocale(locale: string): void {
  document.documentElement.lang = locale
  document.documentElement.dir = isRtl(locale) ? 'rtl' : 'ltr'
}

export async function setLocale(instance: I18n, locale: Locale): Promise<void> {
  await instance.changeLanguage(locale)
  applyDocumentLocale(locale)
  try {
    window.localStorage.setItem(STORAGE_KEY, locale)
  } catch {
    // preference simply isn't remembered
  }
}

export function createI18n(locale: Locale = initialLocale()) {
  const instance = i18next.createInstance()
  void instance.use(initReactI18next).init({
    lng: locale,
    fallbackLng: 'en',
    supportedLngs: [...SUPPORTED_LOCALES],
    resources: { en: { translation: en }, ar: { translation: ar } },
    interpolation: { escapeValue: false },
    returnNull: false,
  })
  applyDocumentLocale(locale)
  return instance
}

