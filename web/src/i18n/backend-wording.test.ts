import { readdirSync, readFileSync, statSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import ar from './locales/ar.json'
import en from './locales/en.json'

// The server answers with stable error codes and finding keys; the browser turns them into words. A code without wording would show
// the person a generic message, so this test fails whenever the backend gains a code that has no wording in both languages.
const BACKEND = join(__dirname, '..', '..', '..', 'src')

function csFiles(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name)
    if (statSync(path).isDirectory()) return name === 'obj' || name === 'bin' || name === 'Migrations' ? [] : csFiles(path)
    return path.endsWith('.cs') ? [path] : []
  })
}

const source = csFiles(BACKEND).map((f) => readFileSync(f, 'utf8')).join('\n')
const collect = (patterns: RegExp[]) => new Set(patterns.flatMap((p) => [...source.matchAll(p)].map((m) => m[1])))

describe('backend wording', () => {
  const errorCodes = collect([
    /(?:ValidationFailedException|ConflictException|NotFoundException)\(\s*"([a-z_]+)"/g,
    /ConfigIssue\(\s*"([a-z_]+)"/g,
    /issues\.Add\(new\(\s*"([a-z_]+)"/g,
    /problems\.Add\(\s*"([a-z_]+)/g,
  ])
  const findingKeys = collect([/Finding\.Of\(\s*"([a-z_.]+)"/g])

  it('finds the codes it is checking (so the check cannot silently pass on nothing)', () => {
    expect(errorCodes.size).toBeGreaterThan(100)
    expect(findingKeys.size).toBeGreaterThan(20)
  })

  it.each([['English', en], ['Arabic', ar]] as const)('every error code the server can send has %s wording', (_name, locale) => {
    const codes = locale.errors.codes as Record<string, string>
    expect([...errorCodes].filter((c) => !codes[c]).sort()).toEqual([])
  })

  it.each([['English', en], ['Arabic', ar]] as const)('every verification finding has %s wording', (_name, locale) => {
    const findings = locale.findings as Record<string, string>
    expect([...findingKeys].filter((k) => !findings[k]).sort()).toEqual([])
  })
})
