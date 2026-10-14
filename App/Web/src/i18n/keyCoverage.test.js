import { describe, it, expect } from 'vitest'
import { readFileSync, readdirSync, statSync } from 'node:fs'
import { join, relative, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
import { i18n, allMessages, SUPPORTED_LOCALES, DEFAULT_LOCALE } from './index'

// D-227: the localization guard (Claude_Docs/Design_Localization.md).
//   1. every key the code uses exists in English;
//   2. every English key is used somewhere (or is in RESERVED below);
//   3. no translation call builds its key at run time (keys must be greppable);
//   4. every registered locale has exactly English's keys, and every locale
//      folder is registered;
//   5. every English message compiles to the text it says (catches an
//      unescaped @ { } | in a message);
//   6. a converted file (one that calls useI18n) has no hard-coded text left
//      in its template or in its static aria-label/title/placeholder/alt.

const SRC = join(dirname(fileURLToPath(import.meta.url)), '..')
const AREAS = ['common', 'shell', 'admin', 'reports', 'pages']
const KEY_PATTERN = new RegExp(`['"\`]((?:${AREAS.join('|')})(?:\\.[A-Za-z0-9_]+)+)['"\`]`, 'g')

// English keys allowed to be unused (e.g. added ahead of the page that will
// use them). Empty once every page is converted; a key that is neither used
// nor listed here fails test 2.
const RESERVED = new Set([])

function sourceFiles(dir = SRC) {
  const files = []
  for (const name of readdirSync(dir)) {
    const path = join(dir, name)
    if (statSync(path).isDirectory()) {
      if (name === 'locales' || name === 'test') continue
      files.push(...sourceFiles(path))
    } else if (/\.(vue|js)$/.test(name) && !name.endsWith('.test.js')) {
      files.push(path)
    }
  }
  return files
}

function flatten(obj, prefix = '') {
  const keys = {}
  for (const [k, v] of Object.entries(obj ?? {})) {
    const key = prefix ? `${prefix}.${k}` : k
    if (v !== null && typeof v === 'object') Object.assign(keys, flatten(v, key))
    else keys[key] = v
  }
  return keys
}

const files = sourceFiles().map(path => ({ path: relative(SRC, path).replaceAll('\\', '/'), text: readFileSync(path, 'utf8') }))
const english = flatten(allMessages[DEFAULT_LOCALE])

const usedKeys = new Map()
for (const file of files) {
  for (const match of file.text.matchAll(KEY_PATTERN)) {
    if (!usedKeys.has(match[1])) usedKeys.set(match[1], file.path)
  }
}

describe('localization keys (D-227)', () => {
  it('has an English message for every key the code uses', () => {
    const missing = [...usedKeys].filter(([key]) => !(key in english)).map(([key, path]) => `${key} (${path})`)
    expect(missing).toEqual([])
  })

  it('uses every English key (or lists it as reserved)', () => {
    const unused = Object.keys(english).filter(key => !usedKeys.has(key) && !RESERVED.has(key))
    expect(unused).toEqual([])
  })

  it('never builds a key at run time', () => {
    const dynamic = files
      .filter(file => /\bt\(\s*`[^`]*\$\{/.test(file.text) || /keypath="`/.test(file.text))
      .map(file => file.path)
    expect(dynamic).toEqual([])
  })

  it('has every area file in English', () => {
    expect(Object.keys(allMessages[DEFAULT_LOCALE]).sort()).toEqual([...AREAS].sort())
  })

  it('registers every locale folder, each with exactly the English keys', () => {
    expect(Object.keys(allMessages).sort()).toEqual([...SUPPORTED_LOCALES].sort())
    for (const code of SUPPORTED_LOCALES) {
      expect(Object.keys(flatten(allMessages[code])).sort(), code).toEqual(Object.keys(english).sort())
    }
  })

  it('compiles every English message to its own text', () => {
    const wrong = []
    for (const [key, source] of Object.entries(english)) {
      // Plural messages: each branch, with {count}/{n} set to the branch's number.
      const branches = source.split(/(?<!\\) \| /)
      branches.forEach((branch, index) => {
        const number = branches.length === 1 ? 7 : index === 0 ? 1 : 7
        const named = {}
        for (const [, name] of branch.matchAll(/\{([A-Za-z_][A-Za-z0-9_]*)\}/g)) named[name] = `<${name}>`
        named.count = String(number)
        named.n = String(number)
        // Placeholders take their values; {'@'} literals and \@ escapes their character.
        const expected = branch
          .replace(/\{'([^']*)'\}|\\([{}@|\\])|\{([A-Za-z_][A-Za-z0-9_]*)\}/g,
            (_, literal, escaped, name) => literal ?? escaped ?? named[name])
        let actual
        try {
          actual = branches.length === 1 ? i18n.global.t(key, named) : i18n.global.t(key, named, number)
        } catch (err) {
          actual = `compile error: ${err.message.split('\n')[0]}`
        }
        if (actual !== expected) wrong.push(`${key}: expected "${expected}", got "${actual}"`)
      })
    }
    expect(wrong).toEqual([])
  })
})

// Text left in a converted file's template: letters outside tags and {{ }},
// and static (unbound) aria-label/title/placeholder/alt attributes.
function hardCodedText(text) {
  const start = text.indexOf('<template>')
  const end = text.lastIndexOf('</template>')
  if (start < 0 || end < 0) return []
  const template = text.slice(start, end).replace(/<!--[\s\S]*?-->/g, '')
  const found = []
  for (const match of template.matchAll(/\s(aria-label|title|placeholder|alt)="([^"]*[A-Za-z][^"]*)"/g)) {
    found.push(`${match[1]}="${match[2]}"`)
  }
  const remaining = template
    .replace(/\{\{[\s\S]*?\}\}/g, ' ')
    // A tag, allowing > inside quoted attribute values (v-if="a > 0").
    .replace(/<[^>"']*(?:"[^"]*"[^>"']*|'[^']*'[^>"']*)*>/g, ' ')
    .replace(/&[a-z]+;|&#\d+;/g, ' ')
  for (const match of remaining.matchAll(/[A-Za-z][A-Za-z'’ ,.:;!?()-]*/g)) {
    found.push(match[0].trim())
  }
  return found
}

describe('converted files (D-227)', () => {
  const converted = files.filter(file => file.path.endsWith('.vue') && file.text.includes('useI18n('))

  it('finds the converted shell files', () => {
    expect(converted.map(file => file.path)).toContain('App.vue')
  })

  it.each(converted.map(file => [file.path, file]))('%s has no hard-coded template text', (_, file) => {
    expect(hardCodedText(file.text)).toEqual([])
  })
})
