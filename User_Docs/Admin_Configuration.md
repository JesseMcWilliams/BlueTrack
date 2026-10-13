# Admin Guide: Configuration

**Blueprint Progress Tracking Web Interface**

## Who this is for

An admin picking up right after [Admin_Installation.md](Admin_Installation.md), working through the settings a real environment normally needs to change before regular users start relying on it day to day. Each step links to the relevant section of `User_Guide.md` for the full field-by-field detail — this guide is the suggested order to work through them in, not a duplicate of that reference.

Everything below lives under **Admin** in the top nav, except where noted.

## 1. Identity Providers

Windows Integrated authentication works out of the box, but a real environment usually needs a real IdP. Add and enable OIDC or SAML under **Admin > Identity Providers** (**+ New Provider**) — see `User_Guide.md`'s Identity Providers section for the exact fields, and `Claude_Docs/Design_Admin-Deployment-Management.md`'s Part 1b for vendor-specific terms (Okta, Entra ID, Ping, Authentik). Remember: enabling/changing OIDC needs an app restart to take effect; SAML changes are read fresh on every request.

In the addresses below, `<your-BlueTrack-hostname>` is the host name users browse to. The API runs under the site's `/BlueTrack` path, which is why that segment appears in the redirect and ACS addresses.

### OIDC

- **From the IdP:** the **Authority** (issuer URL), a **Client ID**, and a client secret, entered in **Secret** (leave it blank later to keep the stored one).
- **To the IdP:** the redirect URI `https://<your-BlueTrack-hostname>/BlueTrack/signin-oidc` (with the default **Callback Path**, `/signin-oidc`). Register it before you enable OIDC, or the IdP rejects the sign-in.
- **Groups:** **Groups Claim Type** (default `groups`) is the claim carrying the user's group names, which you map to roles in section 3.
- **Confirm with a real sign-in after the restart.** If the provider is enabled but no secret is stored, OIDC silently doesn't start, and the health check on **Admin > Deployment** doesn't catch it.

### SAML

- **From the IdP:** the **IdP Entity ID**, the **IdP Single Sign-On Destination** (SSO URL), optionally the **IdP Single Logout Destination**, and the IdP's signing certificate. Import that certificate into the server's `LocalMachine\My` store first, then enter its thumbprint as **IdP Certificate Thumbprint**. Certificates are found by thumbprint; there is no file upload.
- **To the IdP:** the ACS URL `https://<your-BlueTrack-hostname>/BlueTrack/api/auth/saml/acs` (fixed), and the **SP Entity ID** exactly as entered here. If the IdP can import metadata, give it `https://<your-BlueTrack-hostname>/BlueTrack/api/auth/saml/metadata`; it returns metadata only once the SAML provider is enabled with its required fields filled in.
- **SP signing certificate (SP Certificate Thumbprint):** use a certificate made for this purpose, whose private key the App Pool's account can read. Don't reuse the IIS TLS certificate: the App Pool's account normally can't read its private key, and sign-in then fails with a server error.
- **Where certificates are looked up:** first the App Pool account's own store (`CurrentUser\My`), then `LocalMachine\My`. The account's own store suits a certificate that account requested itself, and works only when the App Pool's **Load User Profile** setting is on.
- **Groups:** **Group Claim Type** (default `http://schemas.xmlsoap.org/claims/Group`) must be the attribute name the IdP sends group names under.
- Check the first sign-in against a new IdP by hand: sign in as a test user and confirm the expected role.

### Default sign-in method and direct sign-in addresses

Under **Sign-in** on the Identity Providers page, **Default sign-in method** sets what happens when someone without a session opens BlueTrack. Click **Save**; it applies the next time someone signs in.

- **A method (WindowsIntegrated, OIDC or SAML):** people go straight to it. Windows signs in silently; OIDC and SAML go to the identity provider. Only provider types that have a row are offered, and a disabled one is marked "(disabled)".
- **None (the sign-in page lists the methods):** the sign-in page lists the enabled methods and the user picks one.

Each method also has a **direct sign-in address**, listed under the setting, that always uses that method whatever the default:

- `https://<your-BlueTrack-hostname>/login/windows`
- `https://<your-BlueTrack-hostname>/login/oidc`
- `https://<your-BlueTrack-hostname>/login/saml`

Put the OIDC or SAML address in your IdP's app launch URL (the tile users click in the IdP's portal), or hand it to people who should use that method. Without it, someone arriving from an IdP tile while Windows is the default can end up signed in with Windows instead of SSO, skipping the IdP's MFA. If two enabled providers share a type, the one with the lower **Display Order** is used.

### Worked example: Authentik

This setup has been tried with Authentik. The steps are in BlueTrack's terms; check Authentik's own documentation for its current menu names, which change between versions.

1. **In Authentik, create two applications**: one with an OAuth2/OpenID provider (OIDC) and one with a SAML provider, for example with the slugs `bluetrack-oidc` and `bluetrack-saml`.
2. **OIDC:** in Authentik, add the redirect URI `https://<your-BlueTrack-hostname>/BlueTrack/signin-oidc` and note the client ID and secret. In BlueTrack, add an **OIDC** provider with **Authority** `https://<authentik-host>/application/o/<slug>/` (for example `.../application/o/bluetrack-oidc/`), the **Client ID**, the client secret in **Secret**, and **Groups Claim Type** `groups`. Authentik's `profile` scope carries the `groups` claim, and BlueTrack asks for that scope by default. Restart the app after saving.
3. **SAML:** in the Authentik SAML provider, set the ACS URL to `https://<your-BlueTrack-hostname>/BlueTrack/api/auth/saml/acs` and the **Audience** to the SP Entity ID you enter in BlueTrack (for example `https://<your-BlueTrack-hostname>/BlueTrack`), with POST binding. Pick a signing key for the provider, export its certificate, import it into `LocalMachine\My` on the BlueTrack server, and enter its thumbprint as **IdP Certificate Thumbprint**. Set **IdP Entity ID** to the provider's issuer and **IdP Single Sign-On Destination** to its SSO URL for the redirect binding (`https://<authentik-host>/application/saml/<slug>/sso/binding/redirect/`), and **SP Certificate Thumbprint** to a dedicated SP certificate (see SAML above). Authentik's default SAML group mapping sends group names as `http://schemas.xmlsoap.org/claims/Group`, BlueTrack's default **Group Claim Type**, so include that mapping in the provider.
4. **Groups:** create groups in Authentik such as `BlueTrack-Admin`, `BlueTrack-Analyst`, `BlueTrack-Approver`, `BlueTrack-Auditor` and `BlueTrack-Viewer`, add users, and map each group to its role for each provider (section 3).
5. **Launch URLs:** set each Authentik application's launch URL to its direct address, `https://<your-BlueTrack-hostname>/login/oidc` or `.../login/saml`.
6. **Test:** sign in through each Authentik tile as a test user, and confirm the IdP's MFA prompt and the expected role.

## 2. Secrets Store Configuration

Windows DPAPI is active by default after install. If your organization uses CyberArk CP/CCP/Conjur, Azure Key Vault, or AWS Secrets Manager to resolve privileged-account secrets, cut over under **Admin > Secrets Store Configuration**: fill in the backend's settings and credential, then click **Make Active** (**Save** on the backend that's already active). Exactly one backend is active at a time. Use **Test** under **Test Connection** to confirm a real retrieval works before relying on it.

- **HTTPS only:** the CCP **Base URL**, Conjur **Appliance URL** and Azure **Vault URI** must start with `https://`. Saving an `http://` address is refused, and a backend saved with one earlier isn't used.
- **Credential:** once a credential is stored, its box shows "(already set -- leave blank to keep)". Saving again without retyping it keeps the stored credential.
- **Short CyberArk outages:** for CyberArk CP and CCP, BlueTrack always tries a live fetch first. If CyberArk is briefly unreachable, it uses the last value fetched for that secret within the past 24 hours; a test answered this way says "served from fallback cache". Saving the backend clears these kept values.
- **CyberArk CP needs the SDK:** the CP backend works only when the API was built with the CyberArk Application Password SDK. On a server built without it, the CP row is still listed, but a test reports that no provider implementation is registered; use another backend there.

## 3. Roles & Permissions and Group → Role Mapping

Review the built-in roles under **Admin > Roles & Permissions** and adjust their permission bundles if needed, then map your real groups to them under **Admin > Group → Role Mapping** (you likely already did this once in installation for the Admin role — this is where you map groups for Analyst/Approver/Auditor/Viewer users too).

On **+ Add Mapping**, first choose the **Provider**. For Windows Integrated, enter an AD group as `DOMAIN\GroupName`; BlueTrack resolves it to the group's SID. For OIDC or SAML, enter the group name exactly as the identity provider sends it in its groups claim (the claim set on that provider's Identity Providers page). It isn't checked against the identity provider when saved, so a typo just never matches. The **Lookup / Test Tool** on the same page works for every provider: pick it, enter a group name, and click **Resolve** to see the roles and permissions it currently grants. See [User_Viewer.md](User_Viewer.md), [User_Analyst.md](User_Analyst.md), [User_Approver.md](User_Approver.md), and [User_Auditor.md](User_Auditor.md) for what each role can actually do, to help decide who goes where.

`DeleteAccounts` (delete and undelete accounts in BlueTrack, always with a reason) is granted only to Admin by default. Grant it to another role only for people who should be able to remove accounts from tracking.

## 4. Global Application Configuration

One form, one Save button, covering idle timeout, breadcrumb position, the Exception ID pattern, the Account Progress lock timeout, audit retention, the backup folder, the risk score algorithm, the segregation-of-duties checkbox for Risk Exception approval, the **bulk edit limit** (the most accounts one bulk edit, delete or undelete may change; default 500), the data feed schedule, and the **decommissioned and ignored safe** name patterns (agree your organization's naming convention, such as `DEL_`, `_DECOM` or `ZZ_`, and try it with **Test patterns** before saving). The **Account Progress Lock Timeout (minutes)** must be 1 to 720 (12 hours); new installs start at 30, and an upgrade keeps your existing value. Review every field here at least once — see `User_Guide.md`'s Global Application Configuration section for what each one does.

## 5. Notifications

If you want email alerts, configure SMTP under **Admin > Notifications**, assign target roles to notification types, and add recipients. Use **Send Test Email** to confirm before relying on it.

## 6. Credentials & LDAP

If recipient resolution or AD Account Discovery needs to query a domain, add it under **Admin > Credentials & LDAP** — each domain is independently enabled and bound with its own credential.

## 7. Risk Score Bands and Application ↔ Safe Mapping

Define your Low/Medium/High/Critical (or however you name them) bands under **Admin > Risk Score Bands**, and map your CyberArk Safes to Applications under **Admin > Application ↔ Safe Mapping** — both feed directly into the risk-scoring reports every analyst and approver will see.

## 8. Data Sources

**Admin > Data Sources** (permission `ManageDataSources`, granted to Admin) imports CSV files from folders on a schedule, using the same imports as the Bulk Actions uploads:

- **A feed** names the import it runs (target inventory, access groups, account links, applications, safe assignments or account inventory), an optional mapping profile, a folder and a file name pattern. The pattern may contain today's date in braces, such as `targets_{yyyy-MM-dd}.csv`, and the `*` and `?` wildcards. The newest matching file is imported and left in place.
- **Account inventory feeds** add the accounts another system has (a CSV with `UserName` and `Address`) to Account Progress at **Discovered**, under a **source system** you choose or add on the feed's page. Accounts missing from a later file are marked deleted. Details: [Admin_SettingsReference.md](Admin_SettingsReference.md), "Data Sources (feeds)".
- **Test** shows the file a run would pick up now and its columns, and lists any column the import requires that the file lacks (under the mapping profile's names when one applies).
- **Nightly:** every enabled feed runs in the order shown, at the **Nightly run time** on Global Application Configuration (default 04:00, after the 02:00 Import+Load job). Run history is kept for **Keep run history (days)** (default 15).
- **Run now / Run all** start a run straight away. Inside the **business hours** set on Global Application Configuration (default 07:00-18:00, Monday to Friday), they ask for confirmation first. Only one run happens at a time.
- **History:** each feed's page lists its runs; click a run to see its row errors. Nightly runs are recorded as by the user "BlueTrack Data Feeds (system)", which can't be signed in as; Run now is recorded as by the person who clicked it.
- **Folder access:** feeds are read by the App Pool's account, which needs read access to each folder ([Admin_Installation.md](Admin_Installation.md), "Data feed folders").
- **CyberArk sources**, at the top of the page, are the settings the nightly 02:00 Import+Load job uses: whether to import Privilege Cloud (its export folder and the seven export file names, which may contain date tokens such as `{yyyy-MM-dd}` but not wildcards) and Self-Hosted (its EVD database). Click **Edit CyberArk sources** to change them; **Test** checks that the EVD database, the folder and today's files exist (the SQL Server service account also needs read access to the folder). Changes apply from the next nightly run; there's no Run now. The installer sets them on first install. Each field is described in [Admin_SettingsReference.md](Admin_SettingsReference.md), "CyberArk sources".
- After an upgrade that adds this page, an Admin who was already signed in needs **Reload My Rights** (**Profile & Preferences**, in the menu with their name) to see it.

## What's next

Once these are set, regular use can begin. Ongoing operational tasks — data imports, backup status, audit review — are covered in [Admin_DataManagement.md](Admin_DataManagement.md).

## See also

- `User_Guide.md` — the full field-by-field reference for every Admin hub section.
- `Claude_Docs/Design_Admin-Deployment-Management.md` — the Identity Providers/Secrets Store design rationale and IdP-setup detail.
- `Claude_Docs/Design_Risk-Scoring.md` — the model behind Risk Score Bands, Targets, and Access Groups.
- `Claude_Docs/Design_Notifications.md` — the Notifications page's design rationale.
- `Claude_Docs/Design_Credentials-Management.md` — the Credentials & LDAP page's design rationale.
