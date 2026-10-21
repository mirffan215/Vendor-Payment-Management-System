import { describe, expect, it } from 'vitest'
import ar from './locales/ar.json'
import en from './locales/en.json'

type Tree = { [key: string]: string | Tree }

function flatten(tree: Tree, prefix = ''): Record<string, string> {
  return Object.entries(tree).reduce<Record<string, string>>((acc, [key, value]) => {
    const path = prefix ? `${prefix}.${key}` : key
    return typeof value === 'string' ? { ...acc, [path]: value } : { ...acc, ...flatten(value, path) }
  }, {})
}

const placeholders = (s: string) => [...s.matchAll(/{{\s*(\w+)\s*}}/g)].map((m) => m[1]).sort()

describe('translations', () => {
  const english = flatten(en)
  const arabic = flatten(ar)

  it('Arabic has exactly the same keys as English', () => {
    expect(Object.keys(arabic).sort()).toEqual(Object.keys(english).sort())
  })

  it('has no empty strings', () => {
    for (const [key, value] of [...Object.entries(english), ...Object.entries(arabic)]) {
      expect(value.trim(), key).not.toBe('')
    }
  })

  it('uses the same interpolation placeholders in both languages', () => {
    for (const key of Object.keys(english)) {
      expect(placeholders(arabic[key]), key).toEqual(placeholders(english[key]))
    }
  })

  it('actually translates the text (Arabic strings contain Arabic script) except proper names', () => {
    const properNames = new Set(['language.en', 'login.tenantPlaceholder', 'admin.kind.EntraId', 'admin.kind.Google', 'admin.kind.GenericOidc'])
    for (const [key, value] of Object.entries(arabic)) {
      if (properNames.has(key)) continue
      expect(/[؀-ۿ]/.test(value), key).toBe(true)
    }
  })
})
