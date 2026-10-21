import { readdirSync, readFileSync, statSync } from 'node:fs'
import { join } from 'node:path'
import ts from 'typescript'
import { describe, expect, it } from 'vitest'

const SRC = join(__dirname, '..')
const TEXT_ATTRIBUTES = new Set(['title', 'placeholder', 'alt', 'aria-label', 'aria-description', 'label'])
const hasLetters = (s: string) => /\p{L}/u.test(s)

function sourceFiles(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name)
    if (statSync(path).isDirectory()) return name === 'test' ? [] : sourceFiles(path)
    return path.endsWith('.tsx') && !path.endsWith('.test.tsx') ? [path] : []
  })
}

function findHardCodedText(file: string): string[] {
  const source = ts.createSourceFile(file, readFileSync(file, 'utf8'), ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX)
  const found: string[] = []
  const report = (node: ts.Node, text: string) => {
    const { line } = source.getLineAndCharacterOfPosition(node.getStart())
    found.push(`${file}:${line + 1} "${text.trim()}"`)
  }

  const visit = (node: ts.Node) => {
    if (ts.isJsxText(node) && hasLetters(node.text)) report(node, node.text)
    if (ts.isJsxAttribute(node) && TEXT_ATTRIBUTES.has(node.name.getText()) && node.initializer && ts.isStringLiteral(node.initializer)) {
      if (hasLetters(node.initializer.text)) report(node, node.initializer.text)
    }
    if (ts.isJsxExpression(node) && node.expression && ts.isStringLiteralLike(node.expression) && hasLetters(node.expression.text)) {
      report(node, node.expression.text)
    }
    ts.forEachChild(node, visit)
  }
  visit(source)
  return found
}

describe('internationalisation', () => {
  it('has no hard-coded user-visible strings in components', () => {
    const offenders = sourceFiles(SRC).flatMap(findHardCodedText)
    expect(offenders).toEqual([])
  })

  it('the scanner itself detects hard-coded text', () => {
    const tmp = ts.createSourceFile('x.tsx', '<p title="Hello">Plain text {"literal"}</p>', ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX)
    let hits = 0
    const visit = (n: ts.Node) => {
      if (ts.isJsxText(n) && hasLetters(n.text)) hits++
      if (ts.isJsxAttribute(n) && TEXT_ATTRIBUTES.has(n.name.getText())) hits++
      if (ts.isJsxExpression(n) && n.expression && ts.isStringLiteralLike(n.expression)) hits++
      ts.forEachChild(n, visit)
    }
    visit(tmp)
    expect(hits).toBe(3)
  })
})
