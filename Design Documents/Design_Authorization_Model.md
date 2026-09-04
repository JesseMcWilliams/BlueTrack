# Web Interface Design Document — Authorization Model

**Blueprint Progress Tracking Web Interface**

## Purpose & Scope

Defines how a signed-in identity (from any authentication provider) is mapped to what they're allowed to do in the application. Authorization here is entirely driven by AD/Entra/Okta group membership, normalized to one shape regardless of which provider authenticated the user, and resolved against a database-driven mapping rather than anything hardcoded in application config.

## Design Principles

- Authorization logic never branches on "which provider authenticated this user" — it only ever looks at the normalized group/role claims produced by the authentication layer.
- Group-to-role mapping is data, not code: adding a group to a role is an admin-screen change, not a deployment.
- Prefer a small number of purpose-built, app-specific security groups (e.g., `BlueTrack-Viewers`, `BlueTrack-Approvers`) over reusing broad existing groups — cleaner to reason about, and sidesteps a real technical limit described below.

## Data Model

Revised to a permission-based model: rather than a fixed role hierarchy with assumed cumulative rights, individual permissions exist as their own catalog and get bundled into roles — and, critically, the same permission can be granted through more than one role. This directly supports the decision that exception-approval rights (and similarly scoped capabilities) shouldn't be hardcoded to one specific role.

### app_permission

| Field | Type | Purpose |
|---|---|---|
| PermissionKey | int, PK | Surrogate key |
| PermissionName | text, unique | e.g. `ViewDashboard`, `EditAccountProgress`, `ApproveExceptions`, `ManageIdentityProviders`, `ManageGroupRoleMapping`, `CuratePlatformMapping`, `ConfirmReconciliation`, `ReloadRights`, `ManageRolesAndPermissions`, `CurateApplicationMapping`, `ManageSecretsStore`, `ManageFieldMetadata`, `ViewAuditLog`, `ManageApplicationConfiguration` — **updated 2026-08-27 (D-61)** to cover every admin page in the finalized page inventory (`Design_Application_Structure.md`); the last six were missing on review |
| Description | text | What this permission actually allows, for the admin screen |

### app_role

| Field | Type | Purpose |
|---|---|---|
| AppRoleKey | int, PK | Surrogate key |
| RoleName | text, unique | A named bundle of permissions — not an assumed hierarchy (a role doesn't automatically include a "lower" role's rights unless explicitly given the same permissions) |
| Description | text | What this role is intended to cover, for the admin screen |

### role_permission

| Field | Type | Purpose |
|---|---|---|
| RoleKey | FK to app_role | Part of the composite key |
| PermissionKey | FK to app_permission | Part of the composite key — many-to-many: one role can carry many permissions, and one permission can be granted through more than one role |

### identity_group_role_map

| Field | Type | Purpose |
|---|---|---|
| MappingKey | int, PK | Surrogate key |
| ProviderKey | FK to identity_provider_config | Scopes this mapping to a specific provider (a group name can mean different things across providers) |
| IdentityGroupName | text | The AD/Entra/Okta group identifier, or a DevFakeAuth simulated group name. **Resolved 2026-09-01 (D-69):** for WindowsIntegrated specifically, this is the raw Windows group SID (e.g. `S-1-5-32-544`) read straight off the access token — not a friendly group name. Found ambiguous during implementation (a seeded row used a friendly name instead) and corrected; see `App/Api/Auth/GroupIdentifierExtractor.cs`. The admin screen's lookup/test tool (below) still takes a friendly name and resolves it to the SID server-side, so nobody has to hand-type one. |
| AppRoleKey | FK to app_role | The role granted to members of this group |

A user can belong to more than one mapped group, and therefore hold more than one role simultaneously — confirmed by design decision. A user's effective permissions are the union of every permission reachable through every role granted by every group they belong to, not just their "highest" role.

## Example Permission Bundles

Illustrative starting point, not a fixed requirement — confirm against how your team actually wants to divide responsibility before these are built as the literal default rows.

| Example Role | Permissions Included |
|---|---|
| Viewer | ViewDashboard |
| Analyst | ViewDashboard, EditAccountProgress |
| Approver | ViewDashboard, EditAccountProgress, ConfirmReconciliation, ApproveExceptions |
| Admin | ViewDashboard, EditAccountProgress, ConfirmReconciliation, ApproveExceptions, ManageIdentityProviders, ManageGroupRoleMapping, CuratePlatformMapping, ManageRolesAndPermissions, CurateApplicationMapping, ManageSecretsStore, ManageFieldMetadata, ViewAuditLog, ManageApplicationConfiguration |

Because permissions are bundled per role rather than inherited through a hierarchy, `ApproveExceptions` could just as easily be granted through a narrower, purpose-built role (e.g., an "Exception Approver" role with only that one permission) instead of folding it into a broader Approver role — exactly the flexibility the Risk Exception Tracking design calls for.

## Reload Rights

Since a user can hold multiple roles and roles can change over time, a role/permission change needs a way to take effect without requiring a full logout — but also without re-evaluating group membership on every single request, which has real performance cost. The resolution is an explicit "Reload Rights" action: re-fetches current group membership and rebuilds the active session's permission set on demand, rather than automatically on a timer or per-request.

- **Self-service:** a user can trigger "Reload My Rights" themselves after knowing they were just added to a new group, without waiting for their session to expire.
- **Administrative:** an Admin-permission holder can trigger a reload for another user's active session — useful immediately after correcting a group-role mapping mistake.

**Resolved 2026-08-27:** Reload Rights performs a **live query** against the IdP/AD in real time — it does not refresh from a cached group-membership snapshot. (D-13) Self-service Reload Rights is available to **every user**, not restricted to Admins acting on someone else's session — the administrative reload-for-another-user path remains available in addition to, not instead of, self-service. (D-14)

## Claims Normalization Pipeline

1. Authentication completes; the raw provider-specific claims/token groups are available.
2. A single shared normalization step extracts group identifiers regardless of source shape (Windows token SIDs, an OIDC groups claim, a SAML group attribute).
3. Each normalized group identifier is looked up in `identity_group_role_map`, scoped to the provider that authenticated this session.
4. Every matched role's permissions (via `role_permission`) are unioned together into the session's effective permission set — nothing downstream ever inspects the original provider-specific claim, or even a single role in isolation, again.

## Entra Group Claim Limit — a real constraint, not a hypothetical

Microsoft Entra ID currently caps the number of groups included directly in a token at 100; beyond that, the groups claim is omitted entirely and a separate Microsoft Graph call is required to enumerate membership. If any user who needs this app is a member of more than 100 groups overall, relying on broad, pre-existing groups risks silently losing the groups claim for that user.

**Recommended mitigation:** map only a small number of purpose-built, app-specific security groups (per the Design Principles above) rather than depending on a user's full group list — this avoids the 100-group ceiling being relevant at all, rather than working around it after the fact.

## Admin UI Requirements

- Manage `app_permission` and `app_role` definitions, and which permissions each role bundles (`role_permission`).
- Add/edit/remove `identity_group_role_map` entries, scoped per provider.
- A lookup/test tool: given a group name and provider, show which role(s) and resulting permission set it resolves to — useful for verifying a mapping before relying on it.
- Trigger a Reload Rights action for another user's active session (requires the `ManageGroupRoleMapping` or an equivalent admin permission).

## Implementation Status (added 2026-09-01)

Permission-based authorization is fully built: `GroupIdentifierExtractor` → `AuthorizationRepository` → `PermissionClaimsTransformation` resolves effective permissions on every authenticated request, backing one ASP.NET Core policy per permission (`AuthorizationExtensions`). The Group → Role Mapping admin page's lookup/test tool is built (`WindowsGroupResolver`, using `NTAccount`/`SecurityIdentifier` translation) and was verified resolving `BUILTIN\Administrators` correctly.

**Resolved 2026-09-04 (D-82).** Both gaps above are closed: `UserRightsResolver` now resolves cache-first (`UserRightsCache`, a per-identity entry in `web.distributed_cache` — not an ASP.NET Core cookie session, since Negotiate doesn't need one for anything else this app does) — a cache miss triggers exactly the live re-fetch the design calls for. Self-service Reload Rights (`POST /api/me/reload-rights`) bypasses the cache and refreshes it. The admin-triggered path (`POST /api/admin/users/{userKey}/reload-rights`, gated by the `ReloadRights` permission — matching this section's "an equivalent admin permission") invalidates the target user's cache entry rather than querying AD on the admin's behalf; their own next request re-resolves live via their own Negotiate token. Verified end to end: repeated requests produced exactly one live resolution until an explicit reload or invalidation forced another.

## Open Questions

None remaining as of 2026-08-27 — both prior open questions (live vs. cached Reload Rights; self-service scope) were resolved this session as D-13 and D-14 above.
