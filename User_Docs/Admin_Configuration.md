# Admin Guide: Configuration

**Blueprint Progress Tracking Web Interface**

## Who this is for

An admin picking up right after [Admin_Installation.md](Admin_Installation.md), working through the settings a real environment normally needs to change before regular users start relying on it day to day. Each step links to the relevant section of `User_Guide.md` for the full field-by-field detail — this guide is the suggested order to work through them in, not a duplicate of that reference.

Everything below lives under **Admin** in the top nav, except where noted.

## 1. Identity Providers

Windows Integrated authentication works out of the box, but a real environment usually needs a real IdP. Add and enable OIDC or SAML under **Admin > Identity Providers** — see `User_Guide.md`'s Identity Providers section for the exact fields, and `Claude_Docs/Design_Admin-Deployment-Management.md`'s Part 1b for what to hand your IdP administrator (including vendor-specific notes for Okta, Entra ID, Ping, and Authentik). Remember: enabling/changing OIDC needs an app restart to take effect; SAML changes are read fresh on every request.

## 2. Secrets Store Configuration

Windows DPAPI is active by default after install. If your organization uses CyberArk CP/CCP/Conjur, Azure Key Vault, or AWS Secrets Manager to resolve privileged-account secrets, cut over under **Admin > Secrets Store Configuration** — use **Test Connection** to confirm a real retrieval works before relying on it.

## 3. Roles & Permissions and Group / Role Mapping

Review the built-in roles under **Admin > Roles & Permissions** and adjust their permission bundles if needed, then map your real AD groups to them under **Admin > Group / Role Mapping** (you likely already did this once in installation for the Admin role — this is where you map groups for Analyst/Approver/Auditor/Viewer users too). See [User_Viewer.md](User_Viewer.md), [User_Analyst.md](User_Analyst.md), [User_Approver.md](User_Approver.md), and [User_Auditor.md](User_Auditor.md) for what each role can actually do, to help decide who goes where.

## 4. Global Application Configuration

One form, one Save button, covering idle timeout, breadcrumb position, the Exception ID pattern, the Account Progress lock timeout, audit retention, the backup folder, the risk score algorithm, and the segregation-of-duties checkbox for Risk Exception approval. Review every field here at least once — see `User_Guide.md`'s Global Application Configuration section for what each one does.

## 5. Notifications

If you want email alerts, configure SMTP under **Admin > Notifications**, assign target roles to notification types, and add recipients. Use **Send Test Email** to confirm before relying on it.

## 6. Credentials & LDAP

If recipient resolution or AD Account Discovery needs to query a domain, add it under **Admin > Credentials & LDAP** — each domain is independently enabled and bound with its own credential.

## 7. Risk Score Bands and Application ↔ Safe Mapping

Define your Low/Medium/High/Critical (or however you name them) bands under **Admin > Risk Score Bands**, and map your CyberArk Safes to Applications under **Admin > Application ↔ Safe Mapping** — both feed directly into the risk-scoring reports every analyst and approver will see.

## What's next

Once these are set, regular use can begin. Ongoing operational tasks — data imports, backup status, audit review — are covered in [Admin_DataManagement.md](Admin_DataManagement.md).

## See also

- `User_Guide.md` — the full field-by-field reference for every Admin hub section.
- `Claude_Docs/Design_Admin-Deployment-Management.md` — the Identity Providers/Secrets Store design rationale and IdP-setup detail.
- `Claude_Docs/Design_Risk-Scoring.md` — the model behind Risk Score Bands, Targets, and Access Groups.
- `Claude_Docs/Design_Notifications.md` — the Notifications page's design rationale.
- `Claude_Docs/Design_Credentials-Management.md` — the Credentials & LDAP page's design rationale.
