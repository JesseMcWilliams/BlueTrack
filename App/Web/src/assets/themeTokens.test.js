import { describe, it, expect } from 'vitest'
import { readFileSync, readdirSync, statSync } from 'node:fs'
import { join, relative, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'

// D-306: a color variable the themes don't define falls back to its default in every theme (the Pull details
// dialog stayed white in Dark), so every --color-* a page uses must be one themes.css defines.
const SRC = join(dirname(fileURLToPath(import.meta.url)), '..')

function sourceFiles(dir = SRC) {
  const files = []
  for (const name of readdirSync(dir)) {
    const path = join(dir, name)
    if (statSync(path).isDirectory()) files.push(...sourceFiles(path))
    else if (/\.(vue|css)$/.test(name)) files.push(path)
  }
  return files
}

// A fallback to inherit (the surrounding text color) is harmless, so it is allowed.
const INHERIT_FALLBACK = /var\((--color-[a-z0-9-]+),\s*inherit\)/

describe('theme color variables', () => {
  it('uses only the color variables themes.css defines', () => {
    const themes = readFileSync(join(SRC, 'assets', 'themes.css'), 'utf8')
    const defined = new Set([...themes.matchAll(/(--color-[a-z0-9-]+)\s*:/g)].map(m => m[1]))
    const unknown = []
    for (const file of sourceFiles()) {
      for (const line of readFileSync(file, 'utf8').split('\n')) {
        if (INHERIT_FALLBACK.test(line)) continue
        for (const m of line.matchAll(/var\((--color-[a-z0-9-]+)/g)) {
          if (!defined.has(m[1])) unknown.push(`${relative(SRC, file)}: ${m[1]}`)
        }
      }
    }
    expect(unknown).toEqual([])
  })
})
