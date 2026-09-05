# Web Interface Design Document — Accessibility & Theming

**Blueprint Progress Tracking Web Interface**

## Purpose & Scope

Two related but distinct efforts, requested together (2026-09-04):

1. **Accessibility remediation** — bring the Vue SPA up to WCAG 2.1 Level AA for visually-impaired and screen-reader users. This is a retrofit against an already-built app, not a from-scratch design.
2. **Theming** — a user-selectable Light / Dark / High-Visibility theme system, since none exists today.

The user's explicit direction, given up front: do the full first-pass accessibility remediation (not a partial "quick wins only" pass), persist the theme choice server-side per user (not localStorage-only), and write this design document before implementing — matching how every other cross-cutting feature in this project has been handled.

## Current State (audit findings, 2026-09-04)

Confirmed by reading every `.vue` file in `App/Web/src/`, `router/index.js`, `index.html`, and checking for any global stylesheet:

- **No styling or theming infrastructure exists at all.** No global CSS file, no CSS custom properties, nothing beyond a handful of small `<style scoped>` blocks (`App.vue`'s nav border, `Breadcrumbs.vue`'s font size, the two hub pages' flex layout). Theming is a from-scratch build, not a retrofit.
- **The semantic HTML baseline is solid.** No custom widgets stand in for native ones anywhere — real `<label>` wrapping every `<input>`/`<select>`/`<textarea>`, real `<button>`, real `<table>`/`<thead>`/`<tbody>`, a real `<main>` landmark, `<html lang="en">` set. This matters: it means the remediation below is about filling specific gaps, not unwinding a pile of `<div onclick>` custom controls.
- Five systemic gaps recur across nearly every view, rather than being one-off mistakes in a single page:
  1. **Clickable table rows and sortable column headers have no keyboard support at all.** `<tr @click="...">` (row → navigate to detail) and `<th @click="toggleSort">` (column → re-sort) fire only on pointer events — no `tabindex`, no keydown handler, no ARIA role indicating they're interactive. Affects `AccountProgressList.vue`, `RiskExceptionsList.vue`, `RiskExceptionsOverdueWorklist.vue`, `reports/OverdueAtRiskWorklist.vue`, and `admin/AuditLogViewer.vue` (which also has an expand/collapse row with no `aria-expanded`). Fails WCAG 2.1.1 (Keyboard) and 4.1.2 (Name, Role, Value).
  2. **No `aria-live` region anywhere.** Every view follows the same `<p v-if="loading">Loading...</p>` / `<p v-else-if="error">{{ error }}</p>` pattern for async status — plain text a screen reader has no reason to announce when it appears. Fails SC 3.3.1 (Error Identification) for the error case specifically.
  3. **No skip-to-content link**, and the top nav (`App.vue`) has no `aria-label` — inconsistent with `Breadcrumbs.vue`'s nav, which already has one (`aria-label="Breadcrumb"`). Two `<nav>` landmarks per page with no way for a screen reader user to tell them apart. Fails SC 2.4.1 (Bypass Blocks) and the ARIA APG's landmark-labeling requirement.
  4. **No visible focus indicator anywhere.** Fails SC 2.4.7 (Focus Visible), an AA baseline. There's no CSS resetting `outline: none`, so this is a gap of omission (nothing was ever added), not a regression to undo.
  5. **No route-change focus/announcement.** `router/index.js` has zero navigation guards. In a client-side-routed SPA, that means a screen reader user gets no signal that "the page changed" the way they would on a traditional multi-page site (new page title announced, focus reset to `<body>`) — a well-documented SPA-specific accessibility gap.

## Design Principles

- Target **WCAG 2.1 Level AA** app-wide. The **High-Visibility theme specifically targets AAA contrast** (SC 1.4.6: 7:1 normal text / 4.5:1 large text) rather than merely re-clearing the AA floor (4.5:1/3:1) under a different palette — its whole purpose is exceeding AA, not just relabeling it.
- Prefer native HTML elements and the official **W3C ARIA Authoring Practices Guide (APG)** patterns over hand-rolled keyboard handling — this extends the app's existing convention (real `<label>`/`<select>`/`<button>` throughout) rather than introducing a new one.
- An explicit in-app theme choice always overrides `prefers-color-scheme`, but that media feature still sets the sensible default on a user's first visit before they've chosen anything. Windows/OS-level **Forced Colors Mode** (`forced-colors: active`, what most people mean by "Windows High Contrast Mode") is handled **defensively** — making sure the app degrades gracefully under it — not competed with by imposing a custom palette on top of an OS-level accessibility setting a user has already made. These are two different mechanisms serving two different populations; conflating them was a specific pitfall the research flagged.
- **This is deliberately different from D-57** (breadcrumb position: admin-wide only via Global Application Configuration, explicitly *not* a per-user preference). Theme is per-user by deliberate contrast: a contrast/legibility need is personal, and shouldn't be something an admin sets once for everyone the way a layout preference like breadcrumb position reasonably can be.

## Part 1: Accessibility Remediation

### 1.1 Keyboard & ARIA for sortable columns and interactive rows

**Sortable column headers** (`AccountProgressList.vue`, `RiskExceptionsList.vue`, `admin/AuditLogViewer.vue` all share the identical `toggleSort`/`sortIndicator` pattern): adopt the W3C ARIA APG's [Sortable Table pattern](https://www.w3.org/WAI/ARIA/apg/patterns/table/examples/sortable-table/) —
- Wrap the header's clickable text in a real `<button>` nested inside the `<th>`, filling the header cell. This gets keyboard operability (Space/Enter) and correct semantics from the browser natively — no custom `@keydown` handler needed, unlike the current `@click`-only approach.
- Set `aria-sort="ascending" | "descending" | "none"` on the **`<th>`** itself (not the button) — exactly one column carries a non-`"none"` value at a time; clear the previous one when a new column is sorted.
- Wrap the ▲/▼ glyph in `<span aria-hidden="true">` so it isn't read as part of the button's accessible name, and keep the shape distinction (not just relying on the arrow being visually obvious) so the state is legible for low-vision users too.

**Interactive rows** (row-click-to-navigate, used in the same list/worklist views plus the report worklists, and the audit log's expand/collapse row): native elements first — a real `<a>`/`<button>` per row is strongly preferred over any custom `role`+`tabindex`+keydown wiring, per every source consulted. Where a native element genuinely can't fill the whole row visually, the fallback is `role="link"` (or `"button"` for the audit-log expand/collapse case) + `tabindex="0"` + a keydown handler for Enter/Space — but that's the fallback, not the default. The audit log's expand/collapse row additionally needs `aria-expanded` reflecting its current state.

### 1.2 Live regions for async status

Following the MDN/W3C-documented decision rule:

| Situation | Role |
|---|---|
| Loading state, routine success confirmation (e.g. "Saved.") | `role="status"` (implicit `aria-live="polite"`) |
| Inline validation / async error message | `role="alert"` (implicit `aria-live="assertive"`) |

The region must exist in the DOM before its content changes — rendering a brand-new `role="alert"` element in the same step as the error text isn't reliably announced by all screen readers. Given this exact `loading`/`error` pattern is repeated near-identically across essentially every view in the app, the practical fix is a small shared component (e.g. `AsyncStatus.vue`) wrapping the pattern once, rather than hand-adding `aria-live` to each view individually — lower risk of inconsistency, and matches this project's general preference for a governed, shared implementation over duplicated logic.

### 1.3 Skip link and landmark labeling

- Add a "Skip to main content" link as the first focusable element in `App.vue`, visually hidden until keyboard-focused, targeting `<main>` (which needs `id="main-content"` and `tabindex="-1"` so it can actually receive focus as a link target).
- Add `aria-label="Primary"` (or similar) to `App.vue`'s top `<nav>`, so it's distinguishable from `Breadcrumbs.vue`'s already-labeled one.

### 1.4 Visible focus indicators

App-wide focus-visible styling (SC 2.4.7), using a token from the new theme system (Part 2) so it's consistent across all three themes and gets appropriate contrast per theme automatically — this naturally lands as part of building the theme infrastructure, since there's no existing global CSS to add it to otherwise.

### 1.5 Route-change focus/announcement

Add a global `router.afterEach` hook that (a) moves focus to the new page's top-level heading (or `<main>` if no heading is found) and (b) updates a visually-hidden live region announcing the new page's title — the standard mitigation for the "SPA navigation is silent to assistive tech" gap.

### 1.6 Form error association

The existing `<label>`-wraps-`<input>` pattern is a confirmed-sufficient technique for SC 3.3.2 (Labels or Instructions) — no change needed there. Separately, wherever inline validation error text is shown (e.g. required-field errors), wire `aria-invalid="true"` on the field plus `aria-describedby` pointing at the error text's `id`, so a screen reader announces the error when the field receives focus, not only when it first appears.

## Part 2: Theming

### 2.1 Architecture

CSS custom-property **tokens** (`--color-bg`, `--color-text`, `--color-border`, `--color-link`, `--color-focus-ring`, plus status colors for success/error/warning) defined at `:root`, swapped by a `data-theme="light" | "dark" | "high-contrast"` attribute set on `<html>`. `color-scheme: light dark` is set at `:root` so native browser chrome (scrollbars, form control defaults) follows the light/dark half of the system automatically; the High-Visibility theme isn't a browser "color scheme" in that sense, so it's handled purely through the custom-property layer.

**Exact hex values are an implementation-time detail**, to be checked against a real contrast checker (e.g. WebAIM's) while building each theme's token set, not committed to as literal values in this document — consistent with how this project's other design docs treat schema-level specifics as "illustrative starting point, not a fixed requirement" ahead of actual implementation.

### 2.2 Themes

Three themes ship in this first pass, with base colors confirmed by the user (2026-09-04):

| Theme | Background | Text | Contrast ratio | Target |
|---|---|---|---|---|
| **Light** | current browser-default appearance (white, `#ffffff`) | current browser-default appearance (black, `#000000`) | 21:1 | WCAG AA (SC 1.4.3, 1.4.11) — already far exceeds it |
| **Dark** | `#3c3c3c` | `#f9f9f9` | ~10.5:1 (computed against the relative-luminance formula in SC 1.4.3) | WCAG AA — comfortably clears it, incidentally close to AAA too |
| **High-Visibility** | `#000000` | `#ffff00` | ~19.6:1 | WCAG AAA (SC 1.4.6) |

Secondary tokens (borders, links, focus ring, success/error/warning status colors) aren't specified by the user and are an implementation-time detail — chosen to independently clear each theme's own contrast target (1.4.11's 3:1 for non-text UI) against that theme's background, verified with a contrast checker while building rather than guessed here.

### 2.3 Defaults, overrides, and Forced Colors Mode

- First visit (no stored preference yet): default follows the `prefers-color-scheme` media query (light or dark). There's no equivalent OS signal for "give me High-Visibility," so that theme is only ever reached by explicit user choice.
- Once a user picks a theme explicitly, that choice always wins over the media-query default, and persists (see 2.4).
- `forced-colors: active` (real Windows Forced Colors / High Contrast Mode) is handled defensively, not as a fourth theme: verify the app degrades gracefully under it specifically for the known pitfalls the research flagged — icons/status indicators that rely on `background-color` alone (rather than a real `<svg>`/`<img>` or an actual border) can disappear entirely once the OS remaps backgrounds, and cell/row boundaries that rely on subtle background-color differences rather than a real border can lose their distinction the same way. `forced-color-adjust: none` is available as a narrow, surgical escape hatch (e.g., a logo that must keep its exact colors) but is not a general-purpose tool — fighting a user's own OS-level accessibility choice defeats its purpose. This needs testing against real Windows High Contrast Mode, not just browser DevTools' `forced-colors` emulation, before shipping.

### 2.4 Persistence

The explicit choice is stored **server-side, per user**, in a **generalized preferences table** — the user's explicit correction, 2026-09-04, overriding this document's original single-column proposal — so future per-user settings (beyond theme) don't each need their own schema migration:

### web.user_preference

| Field | Type | Purpose |
|---|---|---|
| UserKey | int, FK to `app_user` | Composite PK part 1 |
| PreferenceKey | nvarchar(50) | Composite PK part 2 — e.g. `'Theme'` |
| PreferenceValue | nvarchar(200) | e.g. `'Light'` \| `'Dark'` \| `'HighVisibility'` |
| ModifiedDate | datetime2 | When this preference was last changed |

A generic self-service endpoint (`PUT /api/me/preferences/{key}`, body `{ value }`) upserts a row — not theme-specific, so any future preference reuses the same table and endpoint shape. `GET /api/me` returns the current user's preferences alongside the existing `roleNames`/`permissionNames` payload, avoiding an extra round trip. The client mirrors the applied theme into `localStorage` purely as an instant-apply cache (so the correct theme paints before the network round-trip completes, avoiding a flash of the wrong theme on load) — `localStorage` is never the source of truth here, only a client-side performance mirror of it.

### 2.5 Theme picker UI

**Confirmed: `MyProfile.vue`** — the user's explicit choice, 2026-09-04. It's already the self-service preferences page (existing "Reload My Rights" action), so the theme selector lives alongside it rather than introducing a second "settings" surface.

## Open Questions

None remaining — theme colors (2.2), persistence shape (2.4), and picker location (2.5) were all confirmed directly by the user, 2026-09-04. Only the derived secondary tokens (borders/links/focus-ring/status colors, none of which were specified) stay an implementation-time detail, checked against a contrast tool while building.

## Implementation Status (2026-09-04)

Both parts are fully built:

**Theming**: `web.user_preference` (`23_BlueTrack_UserPreferenceSchema.sql`), `UserPreferenceRepository`, and `MeController`'s `GET /api/me` (now includes `preferences`) / `PUT /api/me/preferences/{key}` are in place server-side. Client-side: `App/Web/src/assets/themes.css` defines the three themes' tokens (every secondary token's exact contrast ratio computed directly, not estimated — see the comments in that file); `stores/theme.js` applies the theme before mount (`main.js`) from localStorage/`prefers-color-scheme`, then reconciles against the server value once `rights.js`'s `/api/me` call resolves; the picker lives in `MyProfile.vue` as confirmed. Verified end to end via Playwright (`theme.spec.js`): picking a theme applies `<html data-theme>` immediately, and the choice survives a full page reload (proving the server round trip, not just the localStorage mirror).

**Accessibility**: every item from Part 1 is implemented —
- ARIA APG Sortable Table pattern (native `<button>` in `<th>` + `aria-sort`) in `AccountProgressList.vue`, `RiskExceptionsList.vue`, and `admin/AuditLogViewer.vue`.
- Interactive rows converted to real `<router-link>`s in those same three views plus `RiskExceptionsOverdueWorklist.vue` and `reports/OverdueAtRiskWorklist.vue`; `AuditLogViewer.vue`'s expand/collapse row uses a real `<button>` with `aria-expanded`/`aria-controls` instead (a toggle, not a navigation, so the native-link approach didn't fit there).
- `role="status"`/`role="alert"` added to the loading/error/success text pattern across every view that has one (~15 files).
- Skip-to-main-content link and a unique `aria-label` on the top nav, both in `App.vue`.
- App-wide `:focus-visible` styling, defined once in `themes.css` so it's consistent (and correctly contrasted) across all three themes.
- Route-change focus movement + a visually-hidden live-region announcement, via `router.afterEach` in `router/index.js`.
- Section 1.6 (form error association) had no code change beyond the `role="alert"` above: this app's actual error UX is page-level banners, not per-field inline messages, so there was no existing `aria-describedby`-style pattern to retrofit — noted here rather than fabricating one that doesn't reflect the app's real UX.

Existing E2E tests that asserted on the old click-anywhere-in-the-row / bare-`<th>`-click behavior (`list-pages.spec.js`, `reports-pages.spec.js`) were updated to match the new, more accessible markup (click the row's link specifically; assert `aria-sort` directly rather than a rendered arrow glyph in the accessible name). New coverage: `theme.spec.js` (2 tests). Full verification: `dotnet test` 244/244, Vitest 16/16 (including new `theme.test.js`), and every affected Playwright test confirmed individually.

---
*New document added 2026-09-04, following the user's request to research accessibility requirements and add a Light/Dark/High-Visibility theme system, and their explicit direction (asked directly rather than assumed) to do the full accessibility remediation pass, persist theme choice server-side per user, and write this design document before implementing.*
