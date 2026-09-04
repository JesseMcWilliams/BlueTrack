# BlueTrack — Open Questions

Genuine blocking questions found during autonomous `/loop` work (2026-09-04) that this project's own convention says to ask about rather than assume. Each includes enough context to answer without re-deriving it. Answer inline (replace "**Answer:**" with your decision) or discuss and I'll update the design docs/Decision Register accordingly.

---

## 1. `ISecretsProvider`'s shape doesn't fit Windows DPAPI (or likely Azure Key Vault / AWS Secrets Manager either)

**Context:** `App/Api/Secrets/ISecretsProvider.cs` (built for the CyberArk CP integration, D-16/D-76) is shaped around a vault *lookup by reference*: `GetSecretAsync(SecretQuery(Safe, Folder, Object))` → `SecretResult`. That shape fits CyberArk CP/CCP/Conjur and Azure Key Vault/AWS Secrets Manager reasonably well (all are "give me the secret named X" vault lookups, even though each vault's own reference shape differs — a Key Vault secret URI isn't a Safe/Folder/Object triple).

Windows DPAPI is structurally different. It isn't a vault with named objects at all — it's a local encrypt/decrypt primitive (`ProtectedData.Protect`/`Unprotect`) tied to the Windows machine/user context. There's no "Safe" to query; the "reference" is really just the ciphertext blob itself, which BlueTrack would store locally (in `identity_provider_config.SecretReference` or wherever a secret is needed) and hand back to DPAPI to decrypt. Forcing that into `GetSecretAsync(SecretQuery(Safe, Folder, Object))` doesn't fit — there's no Safe or Folder or Object for DPAPI, only an encrypted blob.

**Why this matters now:** D-36 says Windows DPAPI was supposed to be the *first* backend built, before CyberArk CP — it got skipped because CyberArk CP is what real connection details showed up for. Coming back to build DPAPI is what surfaced this mismatch; it wasn't visible with only one (CyberArk-shaped) implementation to check the interface against.

**Options, not a recommendation:**
- Keep one `ISecretsProvider` interface but make `SecretQuery`/`SecretResult` generic enough to cover both shapes (e.g., a single opaque `Reference` string per backend, whose format each provider defines and parses itself) — one interface, format is provider-specific.
- Split into two interfaces/concepts: one for "query an external vault by name" (CyberArk *, Azure KV, AWS SM) and a distinct one for "encrypt/decrypt a locally-stored blob" (DPAPI) — acknowledges they're genuinely different operations rather than forcing a shared shape.
- Something else — e.g., maybe DPAPI doesn't belong behind `ISecretsProvider` at all, and should just be a small standalone `IDpapiProtector`-style helper used directly wherever a locally-stored secret needs protecting, separate from the "query a remote vault" abstraction that D-16 was really describing.

**Answer: Split into two interfaces (2026-09-04).** Done — `IVaultSecretProvider` (CyberArk CP/CCP/Conjur, Azure Key Vault, AWS Secrets Manager) and `ILocalSecretProtector` (Windows DPAPI). See D-79 in the Decision Register and `Design_Secrets_Storage.md`'s Implementation Status. `WindowsDpapiProtector` is built and verified with a real round trip, though nothing calls it yet.

---

## 2. Application-scoped Risk Exception propagation mechanism

**Context:** D-77 wired the Risk Exception workflow into the Account Progress edit form, but only for the account-scoped case. `Design_Risk_Exception_Tracking.md` itself left the application-scoped case explicitly undecided: "For an application-scoped exception, this still resolves per-account through `dim_safe.ApplicationKey` → the accounts in that application's Safes... the exact mechanics of that resolution (a view vs. a batch update) are an implementation detail for later, not decided here."

**Why this matters now:** it's the next natural gap once account-scoped linking works — an Application-scoped exception currently has no effect on any account's `fact_account_progress` row at all.

**Options, not a recommendation:**
- **A view**: compute "is this account currently covered by an application-scoped exception" live, by joining `fact_account_progress` → `dim_safe.ApplicationKey` → `risk_exception` at query time, without ever writing `ExceptionKey` for those accounts. Always fresh, never needs a trigger point, but means `ExceptionKey` alone is no longer a complete picture of "is this account excepted" — code that reads `fact_account_progress.ExceptionKey` directly (there isn't much yet, but the Account Progress edit form is one) would need to also check this view.
- **A batch update**: when an application-scoped exception is created (or revoked/extended), actually update `ExceptionKey` on every `fact_account_progress` row for accounts under that application's Safes. Keeps `ExceptionKey` as the single source of truth, but needs a defined trigger point (on create only? also on Safe reassignment into/out of the application afterward? a periodic reconciliation job?) and doesn't self-heal if a Safe's `ApplicationKey` changes later without someone re-running it.
- Something else.

**Answer: A live view (2026-09-04).** Done — `web.vw_account_application_exception`, exposed via `GET /api/account-progress/{accountKey}/application-exceptions`, shown read-only on the Account Progress edit form. See D-81. Verified both self-healing cases directly: revoking the exception and reassigning the covering Safe to a different Application both removed the coverage immediately.

---

## 3. Session-layer architecture

**Context:** Tracked as "session-layer-dependent follow-ups" in `Design_Decision_Register.md` since 2026-09-01 — four separate-sounding gaps that all trace back to one missing piece: this app has no session/cookie layer at all. Windows Negotiate authenticates per request; nothing persists a session server-side.

**Why this matters now:** it blocks four real, already-designed pieces of behavior:
- D-13's permission resolution is supposed to be cached per session and refreshed only by an explicit Reload Rights action — right now it re-runs the full group→role→permission resolution on every single request.
- D-14's admin-triggered "reload another user's active session" (`Design_Authorization_Model.md`'s Admin UI Requirements) has nothing to target — self-service reload (D-14's other half) works fine since it just re-resolves the caller's own rights.
- D-11's logon auditing isn't wired — there's no way to tell a real logon from a routine `/api/me` call without a session concept, and logging every call would flood the audit log.
- D-35's `audit_config.LogReadEvents` is stored and admin-editable but nothing enforces it — there's no request pipeline hook, and a per-request hook without a session would face the same "every call looks like a fresh event" problem as logon auditing.

**Why this is a real decision, not just an implementation task:** introducing a session layer means picking a mechanism (cookie-based session ID issued after Negotiate completes? a distributed cache/session store for a future load-balanced deployment, or in-process for the confirmed single-server dev environment per D-09? session timeout tied to `app_config.IdleTimeoutMinutes`, which already exists and is admin-configurable but currently enforces nothing since there's no session to time out) — this touches the authentication pipeline itself, which felt like the wrong thing to redesign unprompted.

**Answer: Distributed session store, SQL Server-backed (2026-09-04).** Done — but not as an ASP.NET Core cookie session. "Session" turned out to mean a per-identity cache entry (`UserRightsCache`, `web.distributed_cache` via `Microsoft.Extensions.Caching.SqlServer`), which fully satisfies D-13 (cached permissions) and D-14 (admin reload = invalidate the target's cache entry) without needing cookie/session-ID machinery Negotiate doesn't otherwise need. Logon auditing (D-11) came along as a byproduct: a cache miss is the logon-detection signal. See D-82. **`LogReadEvents` (D-35) enforcement is still not done** — a separate, larger feature (deciding what counts as a loggable "read" across every GET endpoint), not part of this answer.

---

## 4. OIDC / SAML — need real IdP metadata to wire up

**Context:** `App/Api/Auth/AuthenticationExtensions.cs` only registers Windows Integrated. Per D-25, IdP metadata and signing certificates must be pulled once and embedded as local config — never fetched dynamically at runtime — and no real IdP metadata exists for this environment. This isn't something I can resolve myself; it needs real values from you or your identity team (tenant ID/issuer, client ID/secret or certificate, redirect URIs, signing cert thumbprint for SAML).

**Answer (paste real values here, or say when they'll be available):**

---

## 5. Remaining Secrets Storage backends need real service credentials to build and verify

**Context:** Following the same pattern that made CyberArk CP real (you provided AppID/Safe/Object, I inspected the actual SDK, built it, and verified against the live Credential Provider) — Azure Key Vault, AWS Secrets Manager, CyberArk CCP, and CyberArk Conjur each need their own real connection details to do the same:
- **Azure Key Vault**: vault URI, and how this app should authenticate to it (Managed Identity? a service principal?).
- **AWS Secrets Manager**: region, and how this app authenticates (IAM role? access key?).
- **CyberArk CCP**: per D-33, its specific request/response contract was deferred indefinitely with no timeline — worth confirming that's still true before assuming it's out of scope.
- **CyberArk Conjur**: same D-33 deferral.

Without real values, anything built here would be unverified against a real backend — the same gap CyberArk CP had until real values arrived. Also see Question 1 above: Azure Key Vault/AWS Secrets Manager likely fit the `ISecretsProvider` vault-lookup shape reasonably well (each with its own reference format), so that question doesn't block starting these the way it blocks DPAPI specifically.

**Answer (2026-09-04): CyberArk CCP done.** Real details provided (AppID `APP_BlueTrack`, Safe `P-App-User-01`, Folder `root`, the same account object as CP, PVWA URL `https://pvwa.company.com`) — built `CyberArkCcpSecretsProvider` and verified end to end against the real, live CCP service (found at `C:\inetpub\wwwroot\AIMWebService` on this host). See D-80. CCP is now the active backend.

**Azure Key Vault, AWS Secrets Manager, and CyberArk Conjur are still pending** — no real connection details yet.
