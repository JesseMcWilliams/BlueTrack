# BlueTrack: Server-side localization (proposal)

> **Stage: Planning.** Written 2026-10-18 for the backlog item requested 2026-10-14. **Nothing here is decided or built.** It needs the answers in `Planning_Questions-Backlog.md` (question 5) first, above all whether a second language is planned: only English exists today (`SUPPORTED_LOCALES = ['en']`, D-227), so localizing the server now would translate nothing.

## What the server says, and to whom
Counted in `App/Api` on 2026-10-18:

| Text | About | Who reads it | Language it should be in |
|---|---|---|---|
| API errors: `Problem(...)` (about 105), `BadRequest/Conflict/NotFound(new { error })` (about 36), `ApiProblemException` (about 20) | Why a request was refused | The person using the SPA; a script's author | The user's language for the SPA; a script's author reads English |
| Audit details: `auditLogger.LogAsync(... detail:)` (about 100) | What happened, as a record | Auditors, the SIEM, later | One fixed language. A record that changes language with the viewer can't be searched or forwarded consistently |
| Notification emails (5 builders) | Alerts and digests | Recipients, who may have no BlueTrack account or language setting | A setting for the installation, or per recipient |
| Feed run summaries, health check messages, installer text | Operations | Administrators | English is usual |

## Approach proposed
1. **Errors: send a stable code, translate in the SPA.** Add `code` (for example `api.key.name-taken`) and `args` to the ProblemDetails body, beside the existing English `detail`, which stays for scripts and logs. The SPA's fetch wrapper (`App/Web/src/utils/apiBase.js`) looks the code up in `src/locales/<lang>/errors.json` and falls back to `detail`. Translations then live in one place (the SPA's message files, with the key-coverage test D-227 already enforces) and the API needs no resource files, no request-language plumbing and no second copy of every message. Migrate controllers gradually; an error without a code just shows its English `detail`.
2. **Audit details stay English** (decision to confirm). Field names and event types are identifiers already; the Audit Log page can translate the event-type names it shows.
3. **Emails:** a `Language` for the installation (Global Application Configuration) first, since recipients have no language of their own; per-recipient language only if asked. Email builders move their strings to .NET resource files (`.resx`) with `IStringLocalizer`, falling back to English.
4. **Server `Accept-Language`:** only needed for option 3's per-request text (rare). Not built unless asked.

## Phases
1. The `code` and `args` convention, the SPA lookup and the fallback, with the API-key and platform-mapping controllers (new, so cheapest) as the first users, plus a test that every code used by the API has an English message.
2. The remaining controllers, a few at a time (a test lists the `Problem(` calls without a code, to show what is left).
3. Emails, once the installation language exists.

## Not worth doing before a second language exists
Phases 1 and 2 add code and tests but no visible change until a translation is added. If no second language is planned, the better use of the effort is none.
