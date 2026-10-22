# Web Interface Design Document — Authentication Architecture

**Blueprint Progress Tracking Web Interface**

## Purpose & Scope

Defines how the web interface authenticates users through a modular, configurable set of identity providers, without requiring a redeploy to add, remove, or reconfigure a provider. This document does not cover authorization (see `Design_Authorization-Model.md`) or where provider secrets are physically stored (see `Design_Secrets-Storage.md`) — authentication answers only "who is this," not "what can they do" or "where do we keep the credentials."

## Design Principles

- Multiple providers can be enabled simultaneously; the login experience adapts (single sign-on redirect if exactly one provider is enabled, a default-provider redirect with a small link to other options if more than one is — see Login Flow, D-41).
- Enabling, disabling, and reconfiguring a provider is a data change made through an in-app admin screen — not a code deployment or IIS restart.
- Authentication is decoupled from authorization: every provider, regardless of type, ultimately produces the same normalized shape of identity + group claims for the authorization layer to consume.
- The initial provider set is Windows Integrated, OIDC, and SAML, plus a Development-only `DevFakeAuth` provider — designed so a fifth provider type can be added later without restructuring this model.
- The front end is planned to be JS/SPA-based and must work for users on non-Windows client devices (see `Design_Decision-Register.md`, D-21). OIDC and SAML already work from any standards-compliant browser regardless of client OS, so this constraint doesn't change the provider set — it just means Windows Integrated can't be the *only* enabled provider in a mixed-device environment.

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

One row per configured provider instance. A provider type could theoretically be configured more than once (e.g., two different OIDC tenants) if that's ever needed — the model doesn't assume exactly one row per type. Named explicitly here since `Design_Authorization-Model.md` (`identity_group_role_map.ProviderKey`) and `Design_Secrets-Storage.md` (`identity_provider_config.SecretReference`) both reference this table by name.

| Field | Type | Purpose |
|---|---|---|
| ProviderKey | int, PK | Surrogate key |
| ProviderType | text, controlled list | WindowsIntegrated / OIDC / SAML / DevFakeAuth |
| DisplayName | text | Shown on the login/provider-choice screen |
| IsEnabled | bit | Whether this provider is currently active |
| DisplayOrder | int | Controls ordering on a multi-provider login screen |
| ConfigurationValues | structured (see note) | Non-secret settings — tenant ID, issuer URL, redirect URI, entity ID, etc. |
| SecretReference | text, nullable | A pointer into whichever secret store is active — never the raw secret itself (see `Design_Secrets-Storage.md`) |
| CreatedBy / ModifiedBy / ModifiedDate | FK to app_user / FK to app_user / datetime | **Resolved 2026-08-27 (D-59):** were plain text, now FKs — see App User Identity below. Change tracking for an admin-editable security setting |

**Assumption:** `ConfigurationValues` is shown here as a single conceptual field; the actual implementation (a small set of typed columns vs. a structured JSON column) is an implementation detail to settle when this becomes real DDL, not a decision this design document needs to lock in.

## DevFakeAuth Design

Kerberos requires a domain controller; NTLM does not — it validates against the local machine's SAM database. Since ASP.NET Core's Negotiate handler falls back to NTLM automatically, Windows Integrated Authentication already works against a local, non-domain Windows account without any special-casing of the authentication mechanism itself.

DevFakeAuth is therefore not a separate authentication mechanism — it's the same Negotiate handler, with one substitution: instead of resolving group membership from Active Directory/Entra/Okta, it resolves against a small dev-only mapping (local Windows username → simulated app role or group). This means a developer gets a real, OS-verified identity, and can still exercise every authorization path (Viewer, Analyst, Approver, Admin) without needing a domain.

**Guard condition:** DevFakeAuth can only be enabled when the application's hosting environment is Development — enforced in code, not just left as an admin toggle, so it cannot be accidentally enabled in production.

The simulated group mapping lives in the same `identity_group_role_map` table used for real providers (see `Design_Authorization-Model.md`), scoped to the DevFakeAuth provider — no separate mapping mechanism to maintain.

**Implementation Status (added 2026-09-04).** Built and verified end to end. `App/Api/Auth/NegotiateProviderResolver.cs` decides, per request, whether WindowsIntegrated or DevFakeAuth's `identity_group_role_map` rows apply — both authenticate through the same Negotiate handler, so this is the only place that actually distinguishes them, and it enforces the guard condition in code (`IHostEnvironment.IsDevelopment()`), not just DevFakeAuth's own `IsEnabled` toggle. `GroupIdentifierExtractor.GetDevFakeAuthIdentifiers` supplies the authenticated Windows username as the lookup key instead of group SIDs. Seeded via `06_BlueTrack_DevFakeAuthUserMapping.sql` (provider row disabled by default; the username-to-role mapping is a placeholder, same pattern as the WindowsIntegrated admin group seed, since a guessed-at real username would be worse than none).

Verified directly on this dev host: with DevFakeAuth enabled and the current Windows user mapped to a narrow test role, `/api/me` correctly switched from the WindowsIntegrated Admin mapping (14 permissions) to the DevFakeAuth mapping (1 permission) — including resolving to a *distinct* `app_user` row, consistent with `UQ_app_user`'s `(ProviderKey, ExternalIdentifier)` uniqueness. Then, running the compiled app with `ASPNETCORE_ENVIRONMENT=Production` (bypassing `launchSettings.json`, which pins `dotnet run` to Development), the same enabled-and-mapped DevFakeAuth configuration had zero effect — resolution correctly fell back to WindowsIntegrated, proving the guard actually blocks it outside Development rather than only in the common case.

**Scope note:** the Group → Role Mapping admin page's lookup/test tool (`WindowsGroupResolver`, `GroupRoleMappingsController`) stays WindowsIntegrated-only — it's built around AD group name → SID translation, which doesn't apply to DevFakeAuth's plain-username mapping. Adding/removing a DevFakeAuth mapping is direct SQL for now (matching this project's general SQL-first convention for dev-only tooling), not a web admin flow.

**OIDC/SAML Implementation Status (added 2026-09-04, D-85/D-86).** Both are now built as placeholder frameworks — real, working code, but pointed at placeholder configuration since no real IdP metadata exists yet (the user's explicit request: "Use placeholders for now, these will be populated after deployment"). Neither is enabled by default.

- **OIDC** registers `Microsoft.AspNetCore.Authentication.OpenIdConnect` conditionally, reading `web.identity_provider_config` directly at startup (before the DI container exists). `App/Api/Controllers/AuthController.cs` exposes `GET /api/auth/login/oidc` (issues the challenge) and `GET /api/auth/providers` (for a login screen to list enabled providers, unbuilt on the frontend so far). **Deviation from this document's own Design Principle** ("enabling/reconfiguring a provider is a data change... not a code deployment or IIS restart"): OIDC specifically needs an **app restart** to pick up being enabled or reconfigured, since ASP.NET Core registers authentication schemes once at startup and the scoped services (`VaultSecretProviderResolver`) needed to resolve a credential asynchronously don't exist yet at that point. This is a real, deliberately-accepted constraint (documented in `AuthenticationExtensions.cs`), not an oversight — SAML doesn't have this limitation (see below).
- **SAML** is built against `ITfoxtec.Identity.Saml2`/`ITfoxtec.Identity.Saml2.MvcCore` (D-23's selection, previously an unused `csproj` reference) — `App/Api/Controllers/Saml2Controller.cs` (`/api/auth/saml/login`, `/acs`, `/metadata`, `/logout`) and `App/Api/Auth/Saml2ConfigurationFactory.cs`, which builds a `Saml2Configuration` fresh on every request from the `SAML` `identity_provider_config` row — so, unlike OIDC, enabling/reconfiguring SAML takes effect immediately, no restart needed. Both the SP's own signing certificate and the IdP's certificate are looked up by thumbprint in the Windows Certificate Store (D-25/D-34) — never stored as blobs in the database.
- **A serious regression was found and fixed during this work**: registering SAML (`AddSaml2()`) silently overrides `AuthenticationOptions.DefaultScheme`, which broke Windows Integrated authentication entirely. Fixed by forcing the default scheme back to Negotiate immediately after `AddSaml2()` and adding a small fallback middleware (checks the Cookies/SAML session only when Negotiate didn't already authenticate the request) — full details and the verification steps are in D-86.
- **Verified**, not just built: OIDC activated correctly when enabled with placeholder values and the app restarted (a real `Challenge()` reached the OIDC handler, failing only because the placeholder Authority doesn't resolve — proving the scheme registration itself works); SAML's `/metadata` returned genuine signed SP metadata and `/login` produced a fully signed, correctly redirect-bound `AuthnRequest`, using a real temporary self-signed certificate installed to `LocalMachine\My` for the test (removed afterward). Windows Integrated auth was regression-tested repeatedly throughout, including with both OIDC and SAML fully configured.
- **Built 2026-09-05 (D-100)**: `Login.vue` now does real provider redirect logic — fetches `GET /api/auth/providers`, auto-redirects to the default provider (lowest `DisplayOrder`) only when it's OIDC/SAML (an actual external-IdP redirect is needed), and otherwise lists every enabled provider, with WindowsIntegrated/DevFakeAuth shown as non-clickable "(signs in automatically)" entries since neither needs or supports a redirect. **D-173 (2026-10-07)**: when the router guard sends a user here (`?returnUrl=` present) and WindowsIntegrated is the default provider, `Login.vue` re-asks `/api/me` (the rights store had cached the guard's 401) and continues to `returnUrl` (the dashboard by default) once it succeeds; WindowsIntegrated also gets a **Continue** button that does the same, with a message if it still fails. Found live on DCACYBSQL01: the browser finished Windows sign-in, but nothing asked `/api/me` again, so the page stayed on `/login`. A direct visit to `/login` still only lists providers. SAML's Single Logout round trip to the IdP is still not built (`Saml2Controller.Logout` only signs out of this app's own session) — same "no real IdP to test against yet" reasoning as before.
- **D-205: SP certificate lookup** checks the App Pool account's own store (`CurrentUser\My`, needs the pool's Load User Profile) before `LocalMachine\My`, so a certificate that account requested itself works.
- **D-217: SAML Single Logout.** `POST /api/auth/logout` answers 200 `{ singleLogoutUrl }` for a SAML session (it has a `SessionIndex` claim) when `IdpSingleLogoutDestination` is set; the SPA navigates there (`navigateAway`). `GET /api/auth/saml/logout` ends the session and sends a signed `LogoutRequest` (Redirect binding, relay state carries `reason=idle`); the IdP answers at `/api/auth/saml/slo` (GET/POST), which validates the `LogoutResponse` (a failure is only logged; the session has already ended) and redirects to `/login?signedOut=1`. The same endpoint takes an IdP-initiated `LogoutRequest` (front-channel, so the browser brings the cookie), ends the session and answers with a `LogoutResponse`. A request with neither `SAMLRequest` nor `SAMLResponse` is a plain 400 (#120), with nothing sent to the IdP. Back-channel logout isn't supported: cookie sessions can't be ended server-to-server. Proven 2026-10-13 against the lab Authentik 2026.8.2 (SLS URL set, Redirect, front-channel native, signed logout messages): both directions ended the BlueTrack session, and the response validated.
- **D-210: SP key the App Pool account can't read**: signing the AuthnRequest in `Saml2Controller.Login` throws `CryptographicException` ("Keyset does not exist"); it's caught and returned as a 503 "SAML sign-in is not available". `Saml2ConfigurationFactory.SpKeyProblem` signs a test value with the key, and the Identity Providers health check reports Degraded with its reason.
- **First real IdP, 2026-10-12 (lab Authentik 2026.8):** the dev host's OIDC and SAML rows point at Authentik applications `bluetrack-oidc` and `bluetrack-saml`, both signed with Authentik's `AUTH_2026` key (its certificate imported into `LocalMachine\My` for the IdP thumbprint). Windows Integrated stays the default (DisplayOrder 1). Groups `BlueTrack-Admin`/`-Analyst`/`-Approver`/`-Auditor`/`-Viewer` map to the matching roles for both providers (entered with SQL on 2026-10-12; the Add Mapping page can do it since D-204). Two lessons: the SAML SP certificate needs a private key the App Pool account can read (the IIS TLS certificate's isn't, which gave an unhandled 500; the dev host now has a dedicated `CN=BlueTrack SAML SP` certificate); and Authentik's `profile` scope carries the `groups` claim, which BlueTrack requests by default. Verified: both requests are accepted and reach Authentik's sign-in page; a browser sign-in is still to do.
- **Operational/IdP-side setup guidance added 2026-09-23**: the exact redirect URI/ACS URL/SP metadata endpoint values a real IdP needs from BlueTrack, the OIDC secret-missing silent-failure gap, and how these map onto Okta/Entra ID/Ping/Authentik's own terminology — added to `Design_Admin-Deployment-Management.md` once this exact documentation gap was found and asked about directly. Not repeated here, to keep this document's own scope on architecture and decisions rather than operational how-to.

## Login Flow

**D-205 (2026-10-12), how steps 1-2 work now.** The SPA's rights store first asks `GET /api/auth/session` (anonymous, never 401). It calls `/api/me` only when there's already a session or Windows sign-in is wanted: Windows is the default sign-in method, or the user chose it (`/api/auth/login/windows` sets the `BlueTrack.SignIn` session cookie; sign-out clears it). Otherwise it goes to `/login`, because a 401 from `/api/me` makes the browser answer IIS's Windows challenge silently, which is how someone coming from an IdP's app tile was signed in with Windows instead of SSO (found 2026-10-12, so no MFA prompt). The default is an explicit setting (`web.app_config.DefaultSignInProvider`, script 09; the Identity Providers page), replacing D-100's lowest-`DisplayOrder` rule; script 09 sets it to that old implied default, so upgrades behave the same. `/login` starts the default (OIDC/SAML redirect; Windows as D-173) or, with none, lists the enabled methods; `/login/windows`, `/login/oidc`, `/login/saml` use that method whatever the default (an IdP launch URL or a link an admin hands out). The scheme setup in `AuthenticationExtensions.cs` is unchanged. A Windows identity counts as signed in on `/api/auth/session` only while Windows is wanted: IIS keeps Windows authentication on the TCP connection, so after sign-out the same connection still arrives as the Windows user with no Authorization header (found live 2026-10-13). **Verified live 2026-10-13** on the dev site (headless Chromium, Windows auth allowed): with Windows the default, `/` signs in silently and `/login/saml`, `/login/oidc` reach Authentik's sign-in flow; with no default, `/` lists the methods, Continue signs in with Windows, and sign-out stays signed out. A full SSO sign-in with a test user (and MFA) is still for the user to check.

**D-292 (2026-10-21), the last-used method.** With **Remember each browser's last method** on (`web.app_config.RememberLastSignInMethod`, script 52; the checkbox beside the default on the Identity Providers page), `/api/me` writes the persistent `BlueTrack.LastSignIn` cookie (HttpOnly, Secure, Lax, 365 days) with the provider type the user signed in with (WindowsIntegrated, OIDC or SAML; never DevFakeAuth or API access). `GET /api/auth/providers` and `/api/auth/session` then report that method as `defaultProviderType` while it's enabled (`IdentityProviderRepository.GetEffectiveSignInProviderAsync`), else the default, so the SPA needs no change: the sign-in page starts it as it would the default, and a last-used Windows makes the session try Windows. Sign-out keeps the cookie; `/login?signedOut=1` still lists the methods without starting one.

1. User requests a protected page; a genuine 401 from `/api/me` redirects the SPA to `/login?returnUrl=...` (a new `router.beforeEach` guard, D-100 — distinct from an authenticated-but-permissionless user, who gets a 200 with empty rights and is never redirected here).
2. If exactly one provider is enabled, redirect directly to it. **Resolved 2026-08-27 (D-41), superseded 2026-09-05 (D-100):** if more than one provider is enabled, the login screen defaults straight to the provider with the lowest `DisplayOrder` — simpler than this entry's original "last-used per D-02, or an admin-configured system default" proposal, which was never built; the user's explicit choice once `Login.vue` was actually implemented, since no per-user last-used tracking or admin-configurable default setting exists (or was asked for) — a small list still exposes the other enabled providers, including break-glass, for the user who needs a different one. Break-glass users are expected to recognize its label without additional in-app explanation.
3. User authenticates with the chosen provider (silent for Windows Integrated on a domain-joined machine; a redirect/callback round-trip for OIDC/SAML).
4. The provider returns an identity plus whatever raw group/claim information it carries (Windows token groups, an OIDC groups claim, a SAML group attribute).
5. A claims-normalization step (shared code path, not per-provider logic) converts that raw information into one consistent internal shape.
6. **Resolved 2026-08-27 (D-59):** the normalized identity is resolved against `app_user` (upsert: insert on first login, update `LastLogin` on every subsequent one) — see App User Identity below.
7. The normalized identity is checked against `identity_group_role_map` to resolve the user's app role(s) (see `Design_Authorization-Model.md`).
8. Session established; app proceeds using the resolved role and `app_user.UserKey`, never the raw provider-specific claim shape.

## App User Identity

**Resolved 2026-08-27 (D-59).** Several other documents needed a stable way to record "who did this" — the audit log (`Design_Audit-Logging.md`), edit locks (`Design_Data-Editing-Behavior.md`), exception approvals (`Design_Risk-Exception-Tracking.md`) — and none of them is the right place to define it. This is not the same thing as `dim_user` in the main schema, which holds CyberArk vault users pulled from Privilege Cloud/Self-Hosted exports; `app_user` is BlueTrack's own record of people who have logged into this web app.

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
- **Break-glass use alerting:** approved 2026-08-27. Beyond passive capture in the general audit log (D-10/D-11), a break-glass logon triggers an active alert. This fully resolves the audit-of-break-glass question — see former Q-01. (D-24) **The alert-delivery mechanism itself is designed in `Archive_Design_Notification-Framework.md` (D-109, 2026-09-05)** — this decision only established that an alert must fire, not how. **Built 2026-10-13 (D-216), chosen by the user:** no separate sign-in path (the bootstrap Admin role already maps `BUILTIN\Administrators`, so the server's local Administrator signing in with Windows auth is the emergency path). Admins list break-glass accounts on Identity Providers (`web.break_glass_account`, script 16; `DOMAIN\name` stored as its SID). `UserRightsResolver`, on the cache-miss `Logon`, also writes `BreakGlassLogon` and has `BreakGlassAlerter` email the `BreakGlassLogon` notification type's recipients in the background (no cooldown; a send failure is logged, the sign-in proceeds).

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
- Log and alert on any rejected assertion (bad signature, wrong issuer, expired) — this is the canary for a probing attacker, not just a debug log line. (D-26, ties into the general audit/alerting work in `Design_Audit-Logging.md`; the alert-delivery mechanism itself is `Archive_Design_Notification-Framework.md`, D-109)

- **D-198 (2026-10-10):** the SP's entity ID is set as the allowed audience (the audience check was on with an empty list), and replayed responses are rejected (`DetectReplayedTokens` with `SamlTokenReplayCache`, in memory -- one API process on one server). ITfoxtec's defaults validate the IdP certificate chain with online revocation checking. The OIDC/DevFakeAuth sign-in cookie is HTTPS-only.

### Operational

- Since this is a single IdP, an expired certificate nobody rotated is a far more likely failure than an attack (D-27). The **SamlCertificateExpiring** notification (D-287) checks the IdP and SP certificates daily and emails at 30 days, 7 days, when expired and when missing; turn it on and give it recipients.
- Every rejected SAML response and failed OIDC sign-in is audit-logged as LogonFailed and sends the SignInRejected notification, at most one email per 15 minutes (D-285; `SignInRejectionRecorder`, `Saml2Controller`, the OIDC `OnRemoteFailure` event).
- Step-up MFA for admin work is left to the identity provider (D-286, superseding the code half of D-29).

## API access for scripts (D-243)

A script exchanges an API key, or a JWT from an enabled OIDC identity provider (`idToken`), for a 10-minute BlueTrack token at `POST /api/auth/token` and sends it as `Authorization: Bearer <token>`. Built: the keys (`web.api_key`, hash only), the token service (HS256, secret in `web.api_signing_key`, DPAPI-protected), `ApiTokenAuthenticationMiddleware`, the key endpoints and Admin > API Keys (rotation, expiry emails, a per-key rate limit), and the identity provider route (`IdpTokenValidator` against the provider's own discovery metadata, asymmetric algorithms only; `web.api_client_mapping` gives the role by client or by group, lowest order first; the token carries the mapping and role, and each request checks the mapping is still enabled). Design points that matter when changing the sign-in code:
- **It is middleware, not an authentication scheme.** It runs after the cookie-preference middleware and before `UseAuthorization` in `Program.cs`, and does nothing without a bearer header. It adds no default or policy scheme, so Windows, SAML and OIDC sign-in behave as before. Regression-test them after any change here.
- A bearer request is never left to Windows or a cookie: a bad, expired, disabled or deleted key is a 401.
- A key's identity is an `app_user` under the hidden `ApiAccess` provider (`apikey-<n>`), so audit and the rights cache work as for a person. The provider is left out of the sign-in list, the Identity Providers page and the enabled-provider count.
- Rights are the key's (or the mapping's) one role, resolved in `UserRightsResolver` minus `ApiAccessRules.NeverGranted` and never Admin. They are looked up on every request, not cached, and the key's or mapping's state is checked per request from a one-minute cache; admin changes clear it.
- Bearer calls and the token endpoint are exempt from the D-197 header (a browser never sends that header by itself).

## Open Questions

None remaining as of 2026-08-27 — the prior open question on this document (Q-01, break-glass credential custody and audit) was resolved this session as D-24 above. **New item found 2026-09-05 (documentation audit):** D-24 says a break-glass logon "triggers an active alert," but no alert-delivery mechanism was ever built or even designed — see the new `Archive_Design_Notification-Framework.md`, which resolves this alongside two other decisions (D-19, D-26) that made the same assumption.
