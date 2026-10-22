# Web Interface Design Document — Localization

How the SPA's text is localized (D-227), and the conventions for adding or changing text. Every page, component, composable and store is converted; English is the only language so far.

## Scope

- **In scope:** every string the SPA itself shows: text, headings, button and link labels, table headers, option labels, status and error messages the SPA builds (`Request failed: 500`), `aria-label`, `title`, `placeholder` and `alt`.
- **Out of scope (stays as it is):** text from the API (ProblemDetails `detail`/`title`, audit text, feed run summaries, emails, validation messages), data values (names, stage and status values from the database, permission names), CSV column names and their synonyms (`utils/csvHeaderMapping.js`), and identifiers sent to the API. Server-side text is a later phase.
- **English stays byte-identical.** E2E specs find elements by their English text (`getByRole`, `getByLabel`, `getByText`), so a converted page must render exactly the same characters, spaces and punctuation as before.

## How it works

| Piece | Where |
|---|---|
| vue-i18n 11 (MIT), Composition API (`legacy: false`) | `App/Web/src/i18n/index.js` (`i18n`, `t`, `SUPPORTED_LOCALES`, `DEFAULT_LOCALE`) |
| Messages, one file per area | `App/Web/src/locales/<locale>/<area>.json`, merged as `{ <area>: {...} }` |
| Language choice, `<html lang>` | `App/Web/src/stores/locale.js` (`useLocaleStore`), started in `main.js`, server value applied by `stores/rights.js` |
| Picker | Profile & Preferences (`views/MyProfile.vue`), under Theme |
| Number/date/percent format locale | `App/Web/src/utils/formatLocale.js`; `formatCount` uses it |
| Guard | `App/Web/src/i18n/keyCoverage.test.js` |
| Test setup | `App/Web/src/test/setup.js` (installs the plugin on every mount), `App/Web/src/test/i18n.js` (helpers) |

**CSP:** the site's CSP has `script-src 'self'` and no `'unsafe-eval'` (`Deploy/Templates/site-web.config.template`). vue-i18n 10+ compiles a message "JIT" to an AST and interprets it; it never calls `eval` or `new Function` (none in `vue-i18n`/`@intlify/*` dist files, none in our `dist/`). So the full build is used and the CSP is unchanged. There's no build-time precompile: `@intlify/unplugin-vue-i18n` 11 needs Vite 6+, and this app is on Vite 5. The esm-bundler feature flags are set with `define` in `vite.config.js` and `vitest.config.js` (`__VUE_I18N_LEGACY_API__: false`, `__INTLIFY_PROD_DEVTOOLS__: false`, `__INTLIFY_DROP_MESSAGE_COMPILER__: false`, `__VUE_I18N_FULL_INSTALL__: true`).

**Language:** with no saved choice, the first of `navigator.languages` that is registered (exact tag, then primary language), else English. A choice in Profile & Preferences is applied at once, cached in `localStorage` (`bluetrack:language`) and saved with `PUT /api/me/preferences/Language` (`web.user_preference`, D-93; no new table or script). `/api/me` returns it in `preferences.Language` and the rights store applies it. `<html lang>` is set to the active locale.

**Formatting:** `formatCount`, `formatPercent`, `formatShortDate` and `formatDateTime` (`utils/formatLocale.js`) format in the browser's own language (Intl's default, D-218) unless the user explicitly picks a language whose primary subtag differs from the browser's; then they use the chosen language. A page that formats numbers or dates must use these, never `new Intl.NumberFormat(undefined, ...)`, `toLocaleString()` or `toLocaleDateString()` directly. `formatDate` shows a date (the date part of the value, never moved by a time zone) in the same locale's format since D-294; date inputs and values sent to the API stay `yyyy-mm-dd`. Text the server writes for people (emails, feed run and pull summaries) uses one installation-wide culture instead, `web.app_config.FormatCulture` on Global Application Configuration (`App/Api/Formatting/ServerTextCulture.cs`, D-294); audit details, IDs and CSV/Excel exports stay invariant.

## File ownership

Each area is one JSON file; a page's text goes in its area's file:

| File | Covers |
|---|---|
| `locales/en/common.json` | shared words, see below |
| `locales/en/shell.json` | `App.vue`, nav, breadcrumbs (`shell.routes.*`), user menu, About, Login, Profile & Preferences, Access Denied/Not Found, everything in `components/`, `composables/`, stores |
| `locales/en/admin.json` | `views/admin/*` (incl. `dataFeedFormat.js`) |
| `locales/en/reports.json` | `views/reports/*` |
| `locales/en/pages.json` | the rest of `views/*` (Dashboard, Accounts, Exceptions, Targets, Access Groups, bulk pages) |

When parallel work converts several areas at once, each worker edits only its own area's file. A word needed by one page that isn't in `common` goes under that page (`admin.dataSources.refresh`). Candidates noted during the conversion for moving into `common` later: Yes/No, All, Selected, Import, Total, N/A, Dismiss, "Row {row}: {error}", "This cannot be undone."

`common` holds: `save`, `cancel`, `delete`, `edit`, `add`, `remove`, `close`, `clear`, `continue`, `name`, `description`, `status`, `enabled`, `active`, `none` "(none)", `blank` "(blank)", `loading` "Loading...", `importing` "Importing...", `saved` "Saved.", `downloadTemplate`, `exportCsv`, `exportExcel`, `requestFailed` "Request failed: {status}", `saveFailed`, `deleteFailed`, `importFailed`, `exportFailed` (each "… failed: {status}"). Use one only when the English is exactly the same.

## Keys

- `<area>.<page-or-component>.<element>`, camelCase segments: `admin.dataSources.title`, `reports.trends.weekOf`, `pages.targetEdit.saveButton`. The page segment is the file name in camelCase (`AccountProgressList.vue` → `accountProgressList`). Shared words: `common.<element>`.
- One key per distinct English text per page. Don't reuse another page's key for the same words; reuse only `common`.
- **Keys are literal strings in the code**: `t('admin.audit.title')`. Never build one at run time (`` t(`admin.${x}`) `` fails the guard). For a value-to-label map, write each key out: `const STATUS_KEYS = { Active: 'admin.x.statusActive', ... }` then `t(STATUS_KEYS[value])`.
- Route breadcrumbs are `meta.breadcrumb: 'shell.routes.<routeName>'` in `router/index.js` (already done for every route).

## In a component

```vue
<script setup>
import { useI18n } from 'vue-i18n'
const { t } = useI18n()
</script>
<template>
  <h1>{{ t('admin.dataSources.title') }}</h1>
  <button :aria-label="t('admin.dataSources.refreshLabel')">{{ t('common.save') }}</button>
  <input :placeholder="t('admin.dataSources.searchPlaceholder')" />
</template>
```

Outside a component (a store, composable or `.js` helper): `import { t } from '../i18n'` (path relative to the file). Text kept in a `ref` (an error message) is translated when it's set; prefer keeping the key or data and calling `t()` in the template when the text must follow a language change.

## Message syntax (vue-i18n 11)

- **Interpolation:** `"Showing {start}–{end} of {filtered} matching ({total} total)"`, called as `t(key, { start, end, filtered, total })`. Never concatenate translated pieces (`t('a') + count + t('b')`): word order differs between languages. Pass counts already formatted: `{ count: formatCount(n) }`.
- **Plural:** `"{count} match. Use Up and Down to choose. | {count} matches. Use Up and Down to choose."` called as `t(key, { count: n }, n)` (1 picks the first form). Use it only where today's English already varies by number; if today's text is fixed ("This file has {count} columns."), keep it fixed, since English must not change.
- **Markup inside a sentence:** don't put HTML in messages. Use `<i18n-t>` with slots, and always `scope="global"`:

  ```vue
  <i18n-t keypath="shell.bulkFieldEditor.intro" tag="p" scope="global">
    <template #change><em>{{ t('shell.bulkFieldEditor.change') }}</em></template>
    <template #count>{{ formatCount(count) }}</template>
  </i18n-t>
  ```

  with `"intro": "Tick {change} for each field to set on the {count} selected; other fields stay as they are."`.
- **Special characters** `{` `}` `@` `|` (and `$` inside `{}`) are message syntax. To show one, use a literal: `{'@'}`, `{'{'}`, `{'}'}`, `{'|'}`. In JSON: `"Send to admin{'@'}example.com"`. A bare `@` (an email address) is a compile error ("Invalid linked format"); a bare ` | ` turns the message into a plural and shows only part of it. vue-i18n 11.3+ also accepts backslash escapes (`\\@` in JSON, which is `\@` in the message); prefer `{'@'}`, which reads the same in every version. Apostrophes, `$`, `:`, `&`, `–`, `→`, `«` need nothing.
- **Entities:** write the character in JSON (`–`, `«`, `»`, `→`, `&`), not `&ndash;` or `&amp;`.

## Keeping English byte-identical

- Copy the exact visible text, including the trailing period, `...` (three dots, not `…`), `--`, curly or straight quotes, and the double spaces if any.
- **Whitespace next to `{{ }}`:** Vue drops a whitespace-only text node that is the first or last child of an element. `<span> (default)</span>` keeps its space, but `<span> {{ t('x') }}</span>` loses it, and so does `{{ t('x') }} </template>`. Write `<span>{{ ' ' + t('x') }}</span>` or `{{ t('x') + ' ' }}`. A space between an element and `{{ }}` on the same line (`<input /> {{ t('x') }}`) is kept.
- A sentence split by a `v-if` (`… skipped<template v-if="n"> (still selected)</template>.`) becomes two whole messages chosen in the template (`summary`, `summaryStillSelected`), not pieces.
- Run the page's Vitest file and the E2E spec that covers it; both assert on the English text.

## Tests

- `src/test/setup.js` installs the app's i18n instance on every `mount()` and resets English before each test, so existing tests need no change: keep `mount(X, { global: { plugins: [router] } })` and don't add i18n to `plugins` again.
- `src/test/i18n.js`: `tt(key, params)` gives a message's English text for an assertion (prefer asserting the literal English, as E2E does); `resetI18n()` puts the language and format locale back after a test changes them; `i18n` is the instance.
- The guard (`npx vitest run src/i18n`) fails when: a key used in code is missing from English; an English key is unused (except keys listed in `RESERVED`, empty today); a key is built at run time; a registered locale lacks a key English has; a message doesn't compile to its own text (an unescaped `@` or `|`); or a `.vue` file that calls `useI18n(` still has letters in its template outside `{{ }}` and tags, or a static `aria-label`/`title`/`placeholder`/`alt`. Fully convert a file once you add `useI18n` to it. The hard-coded-text check only reads templates; strings in `<script>` are on you.

## Adding a language

1. Copy `src/locales/en/` to `src/locales/<code>/` (BCP 47: `de`, `fr`, `pt-BR`) and translate every value; keep keys, placeholders and plural `|` separators.
2. Have a native speaker review it in the running app (layout, truncation, plural forms; vue-i18n's default plural rule is English-style, so a language with other plural categories needs a `pluralRules` entry in `src/i18n/index.js`).
3. Register it: add `<code>` to `SUPPORTED_LOCALES` in `src/i18n/index.js`. The guard then requires the same keys as English, and the picker lists it by its own name (`Intl.DisplayNames`).
4. Add a line to `User_Docs/Release_Notes.md` and the user docs backlog. No server change: the preference stores any registered code.
