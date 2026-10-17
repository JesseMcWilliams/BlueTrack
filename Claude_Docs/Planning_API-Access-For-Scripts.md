# BlueTrack: API access for scripts and other systems (proposal)

> **Stage: Planning.** Proposal written 2026-10-17 for the backlog item requested 2026-10-13. **Nothing here is decided or built.** The user's answers to the questions at the end become a `D-n` decision, and this doc then becomes `Design_API-Access.md`.

## The need
Scripts and other systems (a scheduler, a SIEM, a CMDB sync) should be able to call BlueTrack's API without a person signing in. Today sign-in is interactive: Windows, SAML and OIDC.

## What already exists, and the constraints it sets
1. **Windows sign-in already works without a person** for a caller that is on the domain: the `Tools/` scripts call the API as the Windows account running them. The gap is callers that are off the domain or not Windows, and a service identity with its own, narrow rights instead of a person's or a broad service account's.
2. **Identity is `web.app_user`**, unique on (`ProviderKey`, `ExternalIdentifier`); rights come from group → role → permission (`identity_group_role_map`, `Design_Authorization-Model.md`) and are cached in SQL (`UserRightsCache`). Every audit row, lock and approval points at `app_user.UserKey`, so a script identity should be an `app_user` too, and then audit, locks and rights work unchanged.
3. **IIS keeps Windows and Anonymous authentication on together** (`BlueTrack.Iis.psm1`), so a request carrying an `Authorization: Bearer` header reaches the app. The app, not IIS, must authenticate it.
4. **`AuthenticationExtensions.cs` is fragile.** A policy scheme and `AddSaml2()` once silently broke Windows sign-in (`Design_Authentication-Architecture.md`). A new scheme must not change the default scheme or forwarding for browser requests, and Windows sign-in must be regression-tested after the change.
5. **D-197:** POST, PUT and DELETE need `X-Requested-With`. It is a CSRF defence for browsers. A bearer-token caller has no ambient credentials to abuse, so it could be exempt, but that is a decision.

## Options
| Option | How it works | For | Against |
|---|---|---|---|
| A. **BlueTrack API keys** (recommended) | An admin creates a key for a named client and gives it a role. The caller sends `Authorization: Bearer <key>`. BlueTrack stores only a hash. | Works for any caller; no IdP changes; one place to manage, expire and audit; rights are an ordinary role. | BlueTrack owns key issue, rotation and leak handling. |
| B. **OIDC client credentials** | The caller gets a JWT from the existing OIDC IdP (Entra, authentik); BlueTrack validates it and maps the client or its groups to a role. | No secrets stored in BlueTrack; the IdP handles rotation and expiry. | Needs an IdP that supports it and an app registration per caller; a second token path in `AuthenticationExtensions.cs`; harder to test live. |
| C. **Windows service accounts only** | Document what works today; add nothing. | No new risk. | Doesn't reach non-domain or non-Windows callers. |

## Proposed design (option A)
- **Table `web.api_key`:** `ApiKeyKey`, `DisplayName` (unique), `KeyPrefix` (first 8 characters, shown in the list and used to find the row), `KeyHash` (SHA-256 of a 256-bit random key; a fast hash is right for a high-entropy key), `AppRoleKey` (one role, like a user), `ExpiresAt` (required), `IsEnabled`, `CreatedBy/At`, `LastUsedAt`, `LastUsedFrom`. The full key (`bt_` plus Base64Url) is shown once, at creation, and never stored.
- **Identity:** a seeded `ApiKey` identity provider; each key has an `app_user` with that provider and `ExternalIdentifier = ApiKeyKey`. The key's role is its rights, resolved directly (no group mapping) and cached like any user's.
- **Authentication:** a bearer handler that runs only when the `Authorization` header starts with `bt_`; it adds no default scheme, no policy scheme and no forwarding. A bad, expired or disabled key is a 401 with no detail.
- **Reach:** the same permissions as the key's role, no more. Keys cannot reach the `ManageApiKeys` or Admin pages whatever the role (a hard rule in code), and have no browser session.
- **Audit:** one `ApiKeyUsed` event per key per hour (first use in the hour), every change a key makes under the normal event types attributed to its `app_user`, and every failed attempt rate-limited and logged as `ApiKeyRejected` with the prefix only. Both flow to SIEM forwarding (D-234).
- **Management:** Admin > API Keys (new permission `ManageApiKeys`): create (name, role, expiry up to 365 days), list (prefix, role, expiry, last used), disable, delete, and **rotate** (a new key alongside the old, so a caller can switch before the old one is disabled). An expiring-key email through the D-222 notification framework (30 days and 7 days before).
- **Rate limiting:** a per-key request limit (default 600 a minute, a setting); over it is a 429.
- **Out of scope:** per-endpoint scopes beyond the role, key IP allow-lists, and a public `/v1` API contract. Each can follow if needed.

## Tests it needs
Unit: key generation and hash check, prefix lookup, expiry. Contract: create, use, disable, expire, rotate; a key cannot reach Admin or key management; a 401 leaks nothing; the rights cache clears on a role change. **Regression: Windows Integrated sign-in, SAML and OIDC unchanged** (`Design_Authentication-Architecture.md`, then `Tools\Test-BlueTrackHealth.ps1` and `Test-BlueTrackSso.ps1` on the dev site). Docs: `Design_Authentication-Architecture.md`, `Design_Authorization-Model.md`, the Admin settings reference, and a short "calling the API from a script" page in `User_Docs/`.

## Phases
1. Table, hashing, the bearer handler, the seeded provider and the rights path, with contract tests. No UI.
2. Admin > API Keys (create, list, disable, delete, rotate) and the audit events.
3. Expiry emails, rate limiting, and the user docs.

## Questions for the user
1. **Option A, B or C?** My recommendation is A.
2. **One role per key, or several?** Proposed: one (simpler to reason about; create another key for another role).
3. **Which roles may a key hold?** Proposed: any except Admin. Is read-only (for example Auditor, Viewer) the only wanted use at first?
4. **CSRF header (D-197):** exempt bearer-token calls from `X-Requested-With`? Proposed: yes, because the header exists to stop browsers from sending ambient credentials.
5. **Maximum lifetime and rotation:** 365 days, with the 30- and 7-day emails? Or shorter?
6. **Who manages keys?** A new `ManageApiKeys` permission held by Admin only?
7. **Audit volume:** the once-an-hour `ApiKeyUsed` event, or an event for every call?
8. **What will call it first?** The answer decides which endpoints to test first and whether a documented, stable contract is needed.
