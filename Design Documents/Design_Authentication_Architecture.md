# Web Interface Design Document — Authentication Architecture

**Blueprint Progress Tracking Web Interface**

## Purpose & Scope

Defines how the web interface authenticates users through a modular, configurable set of identity providers, without requiring a redeploy to add, remove, or reconfigure a provider. This document does not cover authorization (see `Design_Authorization_Model.md`) or where provider secrets are physically stored (see `Design_Secrets_Storage.md`) — authentication answers only "who is this," not "what can they do" or "where do we keep the credentials."

## Design Principles

- Multiple providers can be enabled simultaneously; the login experience adapts (single sign-on redirect if exactly one provider is enabled, a default-provider redirect with a small link to other options if more than one is — see Login Flow, D-41).
- Enabling, disabling, and reconfiguring a provider is a data change made through an in-app admin screen — not a code deployment or IIS restart.
- Authentication is decoupled from authorization: every provider, regardless of type, ultimately produces the same normalized shape of identity + group claims for the authorization layer to consume.
- The initial provider set is Windows Integrated, OIDC, and SAML, plus a Development-only `DevFakeAuth` provider — designed so a fifth provider type can be added later without restructuring this model.
- The front end is planned to be JS/SPA-based and must work for users on non-Windows client devices (see `Design_Decision_Register.md`, D-21). OIDC and SAML already work from any standards-compliant browser regardless of client OS, so this constraint doesn't change the provider set — it just means Windows Integrated can't be the *only* enabled provider in a mixed-device environment.

## Supported Providers

| Provider | Mechanism | Primary Use Case |
|---|---|---|
| Windows Integrated (Negotiate) | Kerberos with NTLM fallback; ASP.NET Core's built-in Negotiate handler | Primary path for domain-joined, intranet-only use |
| OIDC | OpenID Connect against Microsoft Entra ID (or any OIDC-compliant IdP) | Ties into existing Entra SSO/MFA/Conditional Access |
| SAML | SAML 2.0 against Okta (or any SAML IdP) | Ties into existing Okta SSO |
| DevFakeAuth | Real Negotiate/NTLM authentication against a local (non-domain) Windows account, with a substituted group-membership source | Local development off-domain; Development environment only |

SAML has no first-party Microsoft middleware for ASP.NET Core. **Resolved 2026-08-27:** the SAML library will be **ITfoxtec.Identity.Saml2**. (D-23)

## Provider Configuration Data Model

### identity_provider_config

One row per configured provider instance. A provider type could theoretically be configured more than once (e.g., two different OIDC tenants) if that's ever needed — the model doesn't assume exactly one row per type. Named explicitly here since `Design_Authorization_Model.md` (`identity_group_role_map.ProviderKey`) and `Design_Secrets_Storage.md` (`identity_provider_config.SecretReference`) both reference this table by name.

| Field | Type | Purpose |
|---|---|---|
| ProviderKey | int, PK | Surrogate key |
| ProviderType | text, controlled list | WindowsIntegrated / OIDC / SAML / DevFakeAuth |
| DisplayName | text | Shown on the login/provider-choice screen |
| IsEnabled | bit | Whether this provider is currently active |
| DisplayOrder | int | Controls ordering on a multi-provider login screen |
| ConfigurationValues | structured (see note) | Non-secret settings — tenant ID, issuer URL, redirect URI, entity ID, etc. |
| SecretReference | text, nullable | A pointer into whichever secret store is active — never the raw secret itself (see `Design_Secrets_Storage.md`) |
| CreatedBy / ModifiedBy / ModifiedDate | FK to app_user / FK to app_user / datetime | **Resolved 2026-08-27 (D-59):** were plain text, now FKs — see App User Identity below. Change tracking for an admin-editable security setting |

**Assumption:** `ConfigurationValues` is shown here as a single conceptual field; the actual implementation (a small set of typed columns vs. a structured JSON column) is an implementation detail to settle when this becomes real DDL, not a decision this design document needs to lock in.

## DevFakeAuth Design

Kerberos requires a domain controller; NTLM does not — it validates against the local machine's SAM database. Since ASP.NET Core's Negotiate handler falls back to NTLM automatically, Windows Integrated Authentication already works against a local, non-domain Windows account without any special-casing of the authentication mechanism itself.

DevFakeAuth is therefore not a separate authentication mechanism — it's the same Negotiate handler, with one substitution: instead of resolving group membership from Active Directory/Entra/Okta, it resolves against a small dev-only mapping (local Windows username → simulated app role or group). This means a developer gets a real, OS-verified identity, and can still exercise every authorization path (Viewer, Analyst, Approver, Admin) without needing a domain.

**Guard condition:** DevFakeAuth can only be enabled when the application's hosting environment is Development — enforced in code, not just left as an admin toggle, so it cannot be accidentally enabled in production.

The simulated group mapping lives in the same `identity_group_role_map` table used for real providers (see `Design_Authorization_Model.md`), scoped to the DevFakeAuth provider — no separate mapping mechanism to maintain.

**Implementation Status (added 2026-09-04).** Built and verified end to end. `App/Api/Auth/NegotiateProviderResolver.cs` decides, per request, whether WindowsIntegrated or DevFakeAuth's `identity_group_role_map` rows apply — both authenticate through the same Negotiate handler, so this is the only place that actually distinguishes them, and it enforces the guard condition in code (`IHostEnvironment.IsDevelopment()`), not just DevFakeAuth's own `IsEnabled` toggle. `GroupIdentifierExtractor.GetDevFakeAuthIdentifiers` supplies the authenticated Windows username as the lookup key instead of group SIDs. Seeded via `18_BlueTrack_DevFakeAuthSeed.sql` (provider row disabled by default; the username-to-role mapping is a placeholder, same pattern as the WindowsIntegrated admin group seed, since a guessed-at real username would be worse than none).

Verified directly on this dev host: with DevFakeAuth enabled and the current Windows user mapped to a narrow test role, `/api/me` correctly switched from the WindowsIntegrated Admin mapping (14 permissions) to the DevFakeAuth mapping (1 permission) — including resolving to a *distinct* `app_user` row, consistent with `UQ_app_user`'s `(ProviderKey, ExternalIdentifier)` uniqueness. Then, running the compiled app with `ASPNETCORE_ENVIRONMENT=Production` (bypassing `launchSettings.json`, which pins `dotnet run` to Development), the same enabled-and-mapped DevFakeAuth configuration had zero effect — resolution correctly fell back to WindowsIntegrated, proving the guard actually blocks it outside Development rather than only in the common case.

**Scope note:** the Group → Role Mapping admin page's lookup/test tool (`WindowsGroupResolver`, `GroupRoleMappingsController`) stays WindowsIntegrated-only — it's built around AD group name → SID translation, which doesn't apply to DevFakeAuth's plain-username mapping. Adding/removing a DevFakeAuth mapping is direct SQL for now (matching this project's general SQL-first convention for dev-only tooling), not a web admin flow.

**OIDC/SAML Implementation Status (added 2026-09-04, D-85/D-86).** Both are now built as placeholder frameworks — real, working code, but pointed at placeholder configuration since no real IdP metadata exists yet (the user's explicit request: "Use placeholders for now, these will be populated after deployment"). Neither is enabled by default.

- **OIDC** registers `Microsoft.AspNetCore.Authentication.OpenIdConnect` conditionally, reading `web.identity_provider_config` directly at startup (before the DI container exists). `App/Api/Controllers/AuthController.cs` exposes `GET /api/auth/login/oidc` (issues the challenge) and `GET /api/auth/providers` (for a login screen to list enabled providers, unbuilt on the frontend so far). **Deviation from this document's own Design Principle** ("enabling/reconfiguring a provider is a data change... not a code deployment or IIS restart"): OIDC specifically needs an **app restart** to pick up being enabled or reconfigured, since ASP.NET Core registers authentication schemes once at startup and the scoped services (`VaultSecretProviderResolver`) needed to resolve a credential asynchronously don't exist yet at that point. This is a real, deliberately-accepted constraint (documented in `AuthenticationExtensions.cs`), not an oversight — SAML doesn't have this limitation (see below).
- **SAML** is built against `ITfoxtec.Identity.Saml2`/`ITfoxtec.Identity.Saml2.MvcCore` (D-23's selection, previously an unused `csproj` reference) — `App/Api/Controllers/Saml2Controller.cs` (`/api/auth/saml/login`, `/acs`, `/metadata`, `/logout`) and `App/Api/Auth/Saml2ConfigurationFactory.cs`, which builds a `Saml2Configuration` fresh on every request from the `SAML` `identity_provider_config` row — so, unlike OIDC, enabling/reconfiguring SAML takes effect immediately, no restart needed. Both the SP's own signing certificate and the IdP's certificate are looked up by thumbprint in the Windows Certificate Store (D-25/D-34) — never stored as blobs in the database.
- **A serious regression was found and fixed during this work**: registering SAML (`AddSaml2()`) silently overrides `AuthenticationOptions.DefaultScheme`, which broke Windows Integrated authentication entirely. Fixed by forcing the default scheme back to Negotiate immediately after `AddSaml2()` and adding a small fallback middleware (checks the Cookies/SAML session only when Negotiate didn't already authenticate the request) — full details and the verification steps are in D-86.
- **Verified**, not just built: OIDC activated correctly when enabled with placeholder values and the app restarted (a real `Challenge()` reached the OIDC handler, failing only because the placeholder Authority doesn't resolve — proving the scheme registration itself works); SAML's `/metadata` returned genuine signed SP metadata and `/login` produced a fully signed, correctly redirect-bound `AuthnRequest`, using a real temporary self-signed certificate installed to `LocalMachine\My` for the test (removed afterward). Windows Integrated auth was regression-tested repeatedly throughout, including with both OIDC and SAML fully configured.
- **Built 2026-09-05 (D-100)**: `Login.vue` now does real provider redirect logic — fetches `GET /api/auth/providers`, auto-redirects to the default provider (lowest `DisplayOrder`) only when it's OIDC/SAML (an actual external-IdP redirect is needed), and otherwise lists every enabled provider, with WindowsIntegrated/DevFakeAuth shown as non-clickable "(signs in automatically)" entries since neither needs or supports a redirect. SAML's Single Logout round trip to the IdP is still not built (`Saml2Controller.Logout` only signs out of this app's own session) — same "no real IdP to test against yet" reasoning as before.

## Login Flow

1. User requests a protected page; a genuine 401 from `/api/me` redirects the SPA to `/login?returnUrl=...` (a new `router.beforeEach` guard, D-100 — distinct from an authenticated-but-permissionless user, who gets a 200 with empty rights and is never redirected here).
2. If exactly one provider is enabled, redirect directly to it. **Resolved 2026-08-27 (D-41), superseded 2026-09-05 (D-100):** if more than one provider is enabled, the login screen defaults straight to the provider with the lowest `DisplayOrder` — simpler than this entry's original "last-used per D-02, or an admin-configured system default" proposal, which was never built; the user's explicit choice once `Login.vue` was actually implemented, since no per-user last-used tracking or admin-configurable default setting exists (or was asked for) — a small list still exposes the other enabled providers, including break-glass, for the user who needs a different one. Break-glass users are expected to recognize its label without additional in-app explanation.
3. User authenticates with the chosen provider (silent for Windows Integrated on a domain-joined machine; a redirect/callback round-trip for OIDC/SAML).
4. The provider returns an identity plus whatever raw group/claim information it carries (Windows token groups, an OIDC groups claim, a SAML group attribute).
5. A claims-normalization step (shared code path, not per-provider logic) converts that raw information into one consistent internal shape.
6. **Resolved 2026-08-27 (D-59):** the normalized identity is resolved against `app_user` (upsert: insert on first login, update `LastLogin` on every subsequent one) — see App User Identity below.
7. The normalized identity is checked against `identity_group_role_map` to resolve the user's app role(s) (see `Design_Authorization_Model.md`).
8. Session established; app proceeds using the resolved role and `app_user.UserKey`, never the raw provider-specific claim shape.

## App User Identity

**Resolved 2026-08-27 (D-59).** Several other documents needed a stable way to record "who did this" — the audit log (`Design_Audit_Logging.md`), edit locks (`Design_Data_Editing_Behavior.md`), exception approvals (`Design_Risk_Exception_Tracking.md`) — and none of them is the right place to define it. This is not the same thing as `dim_user` in the main schema, which holds CyberArk vault users pulled from Privilege Cloud/Self-Hosted exports; `app_user` is BlueTrack's own record of people who have logged into this web app.

### app_user

| Field | Type | Purpose |
|---|---|---|
| UserKey | int, PK | Surrogate key — the stable identity other tables FK to |
| ProviderKey | FK to identity_provider_config | Which provider authenticated this identity (an external identifier can collide across providers, same reasoning as `identity_group_role_map.ProviderKey`) |
| ExternalIdentifier | text | The raw identifier from the IdP — a Windows SID, an OIDC `sub`/object ID, or a SAML `NameID` |
| DisplayName | text, nullable | For showing "who" in the UI (locks, audit log, approvals) without a join back to the IdP |
| Email | text, nullable | |
| FirstLogin | datetime2 | When this identity first authenticated |
| LastLogin | datetime2 | Updated on every successful login |

`audit_event.PerformedByUserKey`, `account_progress_lock.LockedByUserKey`, and `risk_exception.ApprovedBy` all FK to `app_user.UserKey` going forward — see the respective companion documents, updated accordingly.

## Admin UI Requirements

- List configured providers with enable/disable toggle and display order.
- Add/edit a provider's configuration (non-secret values inline; secret values delegate to whichever secret store backend is active).
- A "test configuration" action per provider before enabling it in a way real users would hit, to catch a bad redirect URI or metadata URL before it locks anyone out.
- Visibility into which provider type is used by which currently-mapped groups, so disabling a provider surfaces the groups that would be orphaned by that change.

## Decisions

- **Break-glass path:** approved. A break-glass authentication path will be included for the scenario where every configured external provider is simultaneously unavailable. (D-01)
- **Provider memory:** approved. The login screen will remember/default to a user's last-used provider rather than always presenting the full choice screen. (D-02)
- **SAML library:** approved. ITfoxtec.Identity.Saml2. (D-23)
- **Break-glass credential custody:** approved 2026-08-27. The break-glass credential is stored securely in the CyberArk product itself (not in this application's own secret store), consistent with treating it as a privileged credential like any other. (D-24)
- **Break-glass use alerting:** approved 2026-08-27. Beyond passive capture in the general audit log (D-10/D-11), a break-glass logon triggers an active alert. This fully resolves the audit-of-break-glass question — see former Q-01. (D-24)

## SAML Security Hardening

Guidance adopted 2026-08-27 for the ITfoxtec.Identity.Saml2 integration (D-23). These apply specifically to the SAML provider; OIDC has its own equivalent trust mechanics (discovery document, JWKS) not covered here.

### Metadata & Certificate Handling

- **Do not fetch IdP metadata dynamically at runtime from a URL.** Pull it once, review it, and embed the IdP's signing certificate (and metadata) as a local config/file in the deployment — not a live HTTP fetch on every startup or request. (D-25)
- A refresh of that embedded metadata (for certificate rotation) is **manually triggered**, not an automatic background poll — convenient for an admin to run when the IdP rotates its cert, but never happening silently on its own. (D-25)
- Any refresh fetch happens over TLS, and the fetched certificate's thumbprint is checked against the thumbprint the admin expects. **Fail closed** (reject auth) if the fetched metadata's certificate doesn't match — never silently trust whatever comes back. (D-25)

### Assertion Validation

- Enforce `SubjectConfirmationMethod` is `bearer` (or whichever method is actually intended to be accepted) — don't accept whatever method is presented. (D-26)
- Enforce `NotBefore`/`NotOnOrAfter` conditions with a small clock-skew allowance (a couple of minutes), not disabled. (D-26)
- Reject unsigned assertions and unsigned responses wherever the IdP is expected to sign either. (D-26)
- Log and alert on any rejected assertion (bad signature, wrong issuer, expired) — this is the canary for a probing attacker, not just a debug log line. (D-26, ties into the general audit/alerting work in `Design_Audit_Logging.md`)

### Operational

- Since this is a single IdP, put a manual calendar reminder (or an automated check) on the IdP's certificate expiration date. A SAML outage from an expired certificate nobody rotated is a far more likely failure mode than an actual attack. (D-27)

## Open Questions

None remaining as of 2026-08-27 — the prior open question on this document (Q-01, break-glass credential custody and audit) was resolved this session as D-24 above.
