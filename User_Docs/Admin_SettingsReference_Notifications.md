# BlueTrack Settings Reference: Secrets, Credentials, Notifications, SIEM Forwarding and API Keys

**For BlueTrack administrators**

This is the second part of the settings reference. The first part, [Admin_SettingsReference.md](Admin_SettingsReference.md), explains how to read the tables and covers Global Application Configuration, sign-in and access, data and risk scoring, the Deployment page and the installer. This part covers the Secrets Store, the credentials BlueTrack signs in with, LDAP, email notifications and forwarding the audit log to a SIEM. Both parts are published together as the Administrator Settings Guide.

## 6. Secrets Store and Credentials

### Secrets Store Configuration

**Admin > Secrets Store Configuration** (permission `ManageSecretsStore`). Exactly one backend is active at a time; Windows DPAPI is active on a new install. Click **Make Active** to switch (after a confirmation: if the new backend isn't set up or can't be reached, lookups fail until another is made active), or **Save** on the active backend. **Test Connection** fetches one secret from the active backend and reports success with the user name and address, never the secret itself. Each test is recorded in the Audit Log as a `ConnectionTest` event, as is each credential **Test** on the Credentials & LDAP page.

| Backend | Settings | Credential column | Recommended when | Why |
|---|---|---|---|---|
| WindowsDpapi | None | Not used | You have no central vault, or for a single server | Built in, no dependency. Secrets are tied to this server and can't be shared with another. |
| CyberArkCP | App ID | Not used | The CyberArk Credential Provider is installed on the server | Retrieval from the local CyberArk agent. Works only if BlueTrack was built with the CyberArk Application Password SDK. |
| CyberArkCCP | Base URL (`https://`), App ID | Not used | You use the CyberArk Central Credential Provider | No local agent needed. |
| CyberArkConjur | Appliance URL (`https://`), Account, Login | The Conjur API key | You use Conjur | The API key is stored encrypted on the server. |
| AzureKeyVault | Vault URI (`https://`), Auth Method (ManagedIdentity or ServicePrincipal), Tenant ID and Client ID for a service principal, Client ID for a user-assigned managed identity | The client secret, for ServicePrincipal only | BlueTrack runs in Azure | Prefer **ManagedIdentity** (the default): no secret is stored at all. |
| AwsSecretsManager | Region, Auth Method (IamRole or AccessKey), Access Key ID for AccessKey | The secret access key, for AccessKey only | BlueTrack runs in AWS | Prefer **IamRole** (the default): no key is stored at all. |

Notes:

- **HTTPS only.** The CCP Base URL, Conjur Appliance URL and Azure Vault URI must start with `https://`; an `http://` address is refused.
- **Credentials are write-only.** A stored credential shows "(already set -- leave blank to keep)". Saving again without retyping keeps it.
- **Short CyberArk outages.** For CyberArk CP and CCP, BlueTrack tries a live fetch first and, if CyberArk is briefly unreachable, uses the last value fetched for that secret within the past 24 hours. Saving the backend clears these kept values.
- Recommended: use your organization's existing vault where you have one, so secrets are managed, rotated and audited in one place. Prefer authentication methods that store nothing on the BlueTrack server (CP, managed identity, IAM role).

### Credentials

**Admin > Credentials & LDAP** (permission `ManageCredentials`). Named credentials that BlueTrack itself signs in with: the SMTP account (or, for OAuth2 email sign-in, the client secret, certificate or key), LDAP bind accounts and the PVWA user for Self-Hosted Direct.

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Name | The credential's name, chosen on the SMTP and LDAP settings. | Blank | Say what it is for, for example "SMTP relay account" | Clear names avoid using the wrong account. |
| Backend | Where the password is kept: WindowsDpapi (on this server) or one of the vault backends. | WindowsDpapi | A vault backend if you have one | The password is then managed and rotated in the vault. |
| Username and Password (WindowsDpapi) | The account and its password, encrypted on this server. Leave the password blank to keep it. For an OAuth2 email credential, only the password is used (see "OAuth2 email sign-in"). | Blank | A dedicated service account | A dedicated account limits what a leaked password can do. |
| DPAPI Scope (WindowsDpapi) | Machine: any process on this server can decrypt it. User: only the App Pool's account can. | Machine | User | User scope keeps other processes on the server, and anyone holding a database backup, from decrypting it. |
| Safe, Folder, Object / Secret Name (vault backends) | Where the credential is found in the vault, using that backend's settings from the Secrets Store page. For CyberArk CP and CCP, a blank Folder means Root. | Blank | As named in your vault | BlueTrack fetches the current password each time it's used. |

A new User-scope credential is stored at Machine scope until the application decrypts it once, then upgrades itself. As with any DPAPI secret, a credential can't be decrypted on a different server, and a User-scope one can't be decrypted after the App Pool's account changes: re-enter the password in those cases. Use **Test** to check a credential.

The list's **Used by** column shows what uses each credential (Notifications, an LDAP domain, the CyberArk sources); a credential in use can't be deleted. With `ManageDataSources`, **Use for Self-Hosted Direct** on a row opens Edit CyberArk sources with that credential chosen.

### LDAP Configuration

On the same page: one row per Active Directory domain or forest BlueTrack queries (notification recipient lookup and AD account discovery).

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Domain Name | The domain this row is for. | `Default` | Your domain's DNS name | Identifies the row. |
| Enabled | Whether this domain is queried. | Off | On for domains you use | Unused rows do nothing. |
| Domain Controller | A specific domain controller. Blank uses the default domain. | Blank | Blank, unless you need a specific controller | Windows then finds an available controller itself. |
| Search Base | Where searches start, for example `DC=company,DC=local`. | Blank | The narrowest container that holds the users and groups needed | Smaller searches are faster and expose less. |
| Use SSL | Connect with LDAP over SSL. | Off | On | Protects directory queries and the bind password in transit. Needs a trusted certificate on the domain controllers. |
| Use trusted connection | Bind as the App Pool's account (or the server's computer account) instead of a stored credential. | Off | On, with a gMSA App Pool identity | No password to store or rotate. |
| Bind Account Credential | The credential to bind with, when not using a trusted connection. | None | A read-only service account | Least privilege. |

**Bind name.** A bind account binds as `user@domain` where BlueTrack can build one: a user name that already holds a domain (`user@domain` or `DOMAIN\user`) is used as it is; otherwise the domain comes from the credential's LogonDomain if it is a DNS name (a CyberArk credential), else its Address if that is a domain name, else the Search Base (`DC=company,DC=com` gives `company.com`). With none of them, the plain user name is used. This matters for an account whose user name is longer than 20 characters, which AD cuts short as its pre-Windows 2000 name.

**Test** on the domain's form binds with the values in the form, saved or not, as discovery would, and shows the name it bound as and the domain controller, or the directory's error. Each test is recorded in the Audit Log as a `ConnectionTest` event.

## 7. Notifications

**Admin > Notifications** (permission `ManageNotifications`). The SMTP settings, which alerts are on, when the scheduled emails go out, and the recipients.

### SMTP Configuration

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| SMTP Host | The mail server. | Blank | Your internal mail relay, or your mail provider's server | Alerts are sent through it. |
| SMTP Port | The mail server port. | 587 | 587 | The standard submission port, used with STARTTLS. |
| Enable STARTTLS | Upgrade the connection to TLS before sending. When off, mail is sent unencrypted. | On | On | Required when a credential is used: saving Basic with a credential, or any OAuth2 method, with STARTTLS off is refused. |
| Auth Method | **None (open relay)**, **Basic (username/password)**, **Microsoft 365 (OAuth2)**, **Google Workspace (OAuth2)** or **Generic OAuth2 (client credentials)**. The page shows only the chosen method's settings. | Basic | Basic with a dedicated credential for an internal relay; the OAuth2 method for your provider's mailbox when username and password sign-in is off there | The relay or provider knows who is sending. Use None only for a relay that accepts only this server. |
| Credential | The credential to sign in with (from Credentials & LDAP). Its label changes with the method (see "OAuth2 email sign-in"). | None | A dedicated mail account or app registration | Least privilege. |
| From Address | The sender address. | Blank | A no-reply or team address, for example `bluetrack@<your-domain>` | Recipients recognise the sender, and replies don't go to a person. |
| From Display Name | The sender name. | Blank | `BlueTrack` | Recipients recognise the alert. |
| Ignore CRL issues | Skip only the certificate revocation check. Host name and chain are still checked. | Off | Off | Turn it on only if your relay's revocation list can't be reached from the server. |
| Ignore all SSL errors | Skip all certificate checks. | Off | Off | It removes the protection TLS gives. Fix the certificate instead. |

Changing the host, port, STARTTLS, "Ignore all SSL errors", authentication method or credential while a credential is in use also needs `ManageCredentials`. With an OAuth2 method, so does changing the tenant, client ID, token URL, scope, audience, sign-in mailbox or From Address, because each of these changes where the stored secret is sent.

### OAuth2 email sign-in

Microsoft 365 and Google Workspace can turn off username and password sign-in for SMTP. The three OAuth2 methods get an access token first and then sign in to the mail server with it (XOAUTH2). **These methods have not yet been proven against a live Microsoft 365 tenant, Google Workspace domain or OAuth2 provider**: they were built from the providers' published documentation (links below). Check with **Send Test Email** before relying on them.

When you first pick an OAuth2 method, the page fills in an empty SMTP Host (`smtp.office365.com` for Microsoft 365, `smtp.gmail.com` for Google Workspace), an empty port with 587, and turns STARTTLS on. Saving clears the settings of the methods you didn't choose.

| Method | Settings on the page | Credential label | What the credential's Password holds |
|---|---|---|---|
| Microsoft 365 (OAuth2) | **Tenant ID** (the tenant's domain, such as `contoso.onmicrosoft.com`, or its GUID), **Client (application) ID**, **Client credential type** (Client secret, or Certificate (PEM certificate and private key)), **Sign-in mailbox** (blank = the From Address) | Client secret credential, or Certificate and key credential | The app registration's client secret; or the certificate in PEM form followed by its unencrypted RSA private key in PEM form |
| Google Workspace (OAuth2) | **Sign-in mailbox** (blank = the From Address): the user in your domain the service account sends as | Service account key credential | The service account's whole JSON key file |
| Generic OAuth2 (client credentials) | **Token URL** (`https://` only), **Client ID**, **Client authentication** (Authorization header (HTTP Basic), or Form fields (client_id and client_secret)), **Scope (optional)**, **Audience (optional)**, **XOAUTH2 username** (blank = the From Address) | Client secret credential | The client secret |

**Storing the secret.** Create the credential on **Admin > Credentials & LDAP** and paste the secret, certificate and key, or JSON key file into its **Password**. Only the password is used; the username isn't. The password box is one line, so line breaks may be lost when pasting; that doesn't matter for these values. The secret is kept like any other credential (encrypted on the server, or in your vault) and never appears on a page, in a log or in the audit log. Use a User-scope DPAPI credential or a vault backend.

**Microsoft 365: what to set up first** (from Microsoft's documentation, listed below; confirm the current portal wording there):

1. Register an application in Microsoft Entra ID. Under its API permissions add the **Office 365 Exchange Online** application permission **SMTP.SendAsApp**, and grant admin consent.
2. Give it a client secret, or upload the certificate whose PEM and key you'll store in the credential.
3. Register its service principal in Exchange Online (Exchange Online PowerShell): `New-ServicePrincipal -AppId <application ID> -ObjectId <object ID>`. Use the Object ID of the **Enterprise Application**, not of the App Registration.
4. Give it access to the sending mailbox: `Add-MailboxPermission -Identity <mailbox> -User <service principal ID> -AccessRights FullAccess`. If the From Address isn't that mailbox, also give it Send As on the From address (`Add-RecipientPermission`).
5. Turn on SMTP AUTH for the mailbox: `Set-CASMailbox -Identity <mailbox> -SmtpClientAuthenticationDisabled $false`. It is off by default for tenants created after January 2020, and can't be used while Entra security defaults are on.

**Google Workspace: what to set up first** (from Google's documentation, listed below):

1. Create a service account in a Google Cloud project, and create a JSON key for it. Store the whole JSON file in the credential.
2. A super administrator adds the service account's numeric client ID under **Security > Access and data control > API Controls > Domain wide delegation** in the Google Admin console, with the scope `https://mail.google.com/`. Google says this can take up to 24 hours to take effect.
3. The sign-in mailbox must be a user in your Google Workspace domain.

Google documents the service-account sign-in and the SMTP sign-in separately, not as one worked example, so this is the least certain of the three methods.

**Generic OAuth2:** register BlueTrack as a client using the client credentials grant at your provider, and set the scope and audience your mail server expects. **Authorization header** is the method every OAuth2 server must support.

**Microsoft's documentation**, read when this was built:

- OAuth for IMAP, POP and SMTP: https://learn.microsoft.com/en-us/exchange/client-developer/legacy-protocols/how-to-authenticate-an-imap-pop-smtp-application-by-using-oauth
- Client credentials flow: https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-client-creds-grant-flow
- Certificate credentials: https://learn.microsoft.com/en-us/entra/identity-platform/certificate-credentials
- SMTP AUTH per mailbox: https://learn.microsoft.com/en-us/exchange/clients-and-mobile-in-exchange-online/authenticated-client-smtp-submission
- SMTP submission settings: https://learn.microsoft.com/en-us/exchange/mail-flow-best-practices/how-to-set-up-a-multifunction-device-or-application-to-send-email-using-microsoft-365-or-office-365

**Google's and other documentation:**

- Service account OAuth and domain-wide delegation: https://developers.google.com/identity/protocols/oauth2/service-account
- XOAUTH2 and the `https://mail.google.com/` scope: https://developers.google.com/workspace/gmail/imap/xoauth2-protocol
- Gmail SMTP server settings: https://knowledge.workspace.google.com/admin/gmail/send-email-from-a-printer-scanner-or-app
- OAuth 2.0 (RFC 6749), client credentials: https://datatracker.ietf.org/doc/html/rfc6749

Tokens are kept in memory until shortly before they expire; if the mail server refuses one, BlueTrack gets a new one and tries once more. Sovereign clouds (Entra hosts other than `login.microsoftonline.com`) aren't supported.

### Notification Types

Each type has an **Enabled** tick box and an optional **Target Role**. A type that isn't enabled is never checked or sent. With a target role, the email also goes to that role's Notification Email and, if LDAP is set up, to members of the role's mapped AD groups, in addition to the recipient list.

| Type | Sent when | What the email holds | On a new install |
|---|---|---|---|
| NightlyImportFailed | Within about 5 minutes of the nightly Import, Load or daily snapshot failing, or of a run still not finished 4 hours after it started. Once per run. | The step, when it started and failed (server time), the error with the procedure and line, the import files that failed in that run, and who started it. | Off |
| OverdueRiskExceptions | Once a day, from the daily digest time, when any Risk Exception is past its review date. Nothing is sent when none are. | The Overdue Reviews list (up to 100). | Off |
| WeeklySummary | Once a week, on the weekly summary day, from its time. | The KPI Summary figures; % complete and accounts per stage compared with the snapshot a week older ("No history yet" when there is none); overdue and at-risk accounts; exceptions overdue for review; the last Import, Load and snapshot runs. | Off |
| ApiKeyExpiring | Once a day, from the daily digest time, to each API key's **owner email**: the reminder lead time (7 days by default) before the key expires, and on the day it expires. Keys with no owner email are skipped. | The key's name, the first characters of the key, its role and expiry date, with a link to Admin > API Keys. Sent to the owner only, not to the notification recipients. | Off |
| ApiKeyExpired | Once a day, from the daily digest time, for each API key that expired that day. | The same, and whether the owner was told. Sent to the administrators (this type's recipients and target role). | Off |
| SelfHostedPullFailed | Within about 5 minutes of a Self-Hosted Direct pull failing, being rejected (the Safe or account count changed more than allowed), or still not finished 2 hours after it started. Once per pull. | When it started and who started it, the outcome, the Safe and account counts before and now, the pull's messages (a rejected pull's include the Safes no longer returned), what it means and what to do, with a link to Data Sources. | Off |
| DevFakeAuthEnabledTooLong | When DevFakeAuth has been enabled for more than 7 days; repeats at most every 7 days. | How long DevFakeAuth has been enabled, and since when. | On |
| CpmHealth | Once a day, from the daily digest time, when any account's CPM status became failure since the last CpmHealth email. Not sent when there are none. | The newly failing accounts (up to 100) with Safe, platform and last CPM action; Reports > CPM Health lists them all. | Off |
| SignInRejected | At once when a SAML or OIDC sign-in is rejected (bad signature, audience, time or replay, or the identity provider refused it); at most one email per 15 minutes, the next counting the ones in between. Every rejection is in the Audit Log as LogonFailed either way. | When, the reason, the source IP, and how many more since the last email. | Off |
| SamlCertificateExpiring | Once a day, from the daily digest time, when the SAML identity provider's certificate or BlueTrack's own SAML certificate expires within 30 days, within 7 days, has expired, or isn't installed. Once per certificate and step. | Which certificate, its thumbprint, subject and expiry, and what to do. | Off |
| BreakGlassLogon | At once, every time a designated break-glass account signs in (Admin_SettingsReference.md, Identity Providers, "Break-glass accounts"). | The account, the sign-in method, the source IP address and the time (UTC), and where to find it in the Audit Log. | On |

Recommended: enable NightlyImportFailed on every real environment, since nothing else in BlueTrack tells you a nightly import failed. With Self-Hosted Direct, enable SelfHostedPullFailed too: it arrives before the import that the failed pull will make fail. Set every type's target role to your Admin role, or to a role whose Notification Email is a monitored team mailbox. NightlyImportFailed doesn't cover a job that never starts at all (SQL Server Agent stopped, or the job disabled); `Tools\Test-BlueTrackHealth.ps1` checks those.

**Emails** have a subject starting `BlueTrack:`, and an HTML and a plain-text version with the same content. When **Site URL** is set on Global Application Configuration, they link to the pages they mention (for example Overdue Reviews, Trends, KPI Summary, CyberArk Sources, Audit Log); without it they name the pages.

### Schedule

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Daily digest time | When the daily emails (OverdueRiskExceptions, ApiKeyExpiring) go out, server local time. | 07:00 | Before your team's working day starts | The list is waiting when people arrive. If the server is down at that time, the email goes out when it's back the same day. |
| Weekly summary day | The day WeeklySummary goes out. | Mon | The first working day of your week | Starts the week with last week's progress. |
| Weekly summary time | The time it goes out that day, server local time. | 07:00 | Same as the daily digest | One time is easier to remember. |

Click **Save Schedule** to save the schedule; it is saved separately from the SMTP settings, and every change is recorded in the audit log. Each scheduled email goes out once per day or week, even if the API restarts.

### Recipients and testing

**Recipients.** Each recipient has an **Email**, an optional **Display Name** and an **Active** flag. Add at least one monitored mailbox, or give each type a target role; with neither, alerts go nowhere.

**Send Test Email** sends a real message to every active recipient using the saved settings. Use it after every SMTP change. Each test is recorded in the Audit Log as a `ConnectionTest` event, with the authentication method and whether it worked. A failed OAuth2 test says what went wrong, for example "Token endpoint returned invalid_client".

## 8. SIEM Forwarding

**Admin > SIEM Forwarding** (permission `ManageApplicationConfiguration`, the same as the audit settings on Global Application Configuration). Sends every new audit log event to your SIEM's syslog collector as a CEF (Common Event Format) message inside RFC 5424 syslog, over TLS or TCP, within a few seconds. Off on a new install.

**What is sent.** Only events written after forwarding is turned on; older events aren't sent. If forwarding is turned off and on again, it starts again from then. Events wait in the database while the collector can't be reached and go out, oldest first, when it's back; after an API restart forwarding carries on where it stopped. Each event goes out at least once: after a failure a batch is sent again whole, so the SIEM may occasionally see an event twice (the same `externalId`). While forwarding is on, the Audit Log Purge job keeps events not yet sent, however old.

### Collector and TLS

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Forward the audit log to the SIEM | Turns forwarding on or off. Needs a Host. | Off | On, once **Send Test Event** works | Keeps a copy of the audit trail outside BlueTrack's database, where BlueTrack's administrators can't change it. |
| Host | The collector's host name or IP address. | Blank | The collector's DNS name | A name matches the certificate the collector presents over TLS. |
| Transport | **TLS (RFC 5425)** or **TCP, not encrypted (RFC 6587)**. There is no UDP. | TLS | TLS | Audit events name users, accounts and addresses; TCP sends them in clear text. |
| Port | 1-65535. Changing the transport switches between the default ports. | 6514 (TLS), 514 (TCP) | Your collector's port | 6514 is the registered syslog-over-TLS port. |
| Framing | **Octet counting** (each message preceded by its length) or **Line feed** after each message. | Octet counting | Octet counting, unless your collector needs line feed | RFC 5425 specifies octet counting for TLS, and it can't be confused by the message's content. |
| Trust the collector's certificate by | **The Windows trust store** (the certificate must chain to a CA the server trusts and name the Host); **Its fingerprint** (exactly this certificate, for a self-signed collector); **A CA certificate given here** (paste the CA's PEM; only that CA is trusted, and the certificate must name the Host). TLS only. | The Windows trust store | The trust store, or your internal CA | Fingerprint pinning works, but must be updated whenever the collector's certificate is renewed. |
| Certificate fingerprint | The collector certificate's SHA-256 (64 hex digits) or SHA-1 (40), with or without colons, optionally starting `sha-256:` or `sha-1:`. | Blank | SHA-256 | SHA-1 is accepted because RFC 5425 requires it, but SHA-256 is stronger. |
| CA certificate (PEM) | The CA that issued the collector's certificate, `-----BEGIN CERTIFICATE-----` to `-----END CERTIFICATE-----`. | Blank | Your issuing CA | Trusts only that CA, not everything in the Windows store. |
| Check certificate revocation | Checks online whether the certificate was revoked (not with a fingerprint). | On | On | Turn it off only if the server can't reach the CA's revocation list and you accept the risk; a check that can't complete fails the connection. |

There's no option to ignore certificate errors. For a collector with a self-signed certificate, pin its fingerprint.

### Message

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Syslog facility | The syslog facility (0-23) in each message's priority. | 20 - local4 | What your SIEM routes on | local0-7 are meant for local use, so they don't collide with the operating system's own messages. |
| Syslog host name | The HOSTNAME in each message. Blank uses this server's full name. | Blank | Blank, unless several servers should report as one name | The SIEM shows where the events came from. |
| App name | The APP-NAME in each message; up to 48 characters, no spaces. | BlueTrack | BlueTrack | Lets SIEM rules pick out BlueTrack's events. |
| Timeout (seconds) | How long to wait to connect, for the TLS handshake and for each write. 1-120. | 10 | 10 | Long enough for a remote collector; a dead one is noticed quickly. |
| Largest message (bytes) | 2048-65535. A longer message drops its field-change summary first, then has its detail and reason shortened. | 8192 | 8192, or what your collector accepts | RFC 5425 says collectors should accept 8192 bytes and must accept 2048. |

**Each message** holds: the time (UTC, to the millisecond), vendor and product `BlueTrack`, the BlueTrack version, the event type (as the CEF signature ID and the syslog MSGID), its description, the severity, and as CEF fields the audit event number (`externalId`), the user (`suser`, `suid`), the source IP address (`src`; an IPv6 address in `c6a1`), the entity and its key (`cs1`, `cs2`), a summary of the field changes (`cs3`, up to 1,000 characters), the reason and the detail (`msg`, up to 1,023 characters).

### Event types

Each audit event type has a **Forward** tick box and a **Severity** (CEF, 0-10: 0-3 low, 4-6 medium, 7-8 high, 9-10 very high). All are forwarded on a new install. The syslog severity follows: 0-3 Informational, 4-6 Notice, 7-8 Warning, 9-10 Critical. **Forward All** and **Forward None** tick or clear every row.

| Event type | Default severity |
|---|---|
| BreakGlassLogon | 8 |
| LogonFailed | 6 |
| ExceptionApproved, ExceptionReviewExtended, ExceptionRevoked, ExceptionImported, ProviderConfigChanged, ReloadRights, AccountDeleted, AccountUndeleted, ConnectionTest, ReportExport | 5 |
| BulkEdit | 4 |
| Logon, FieldEdit, EntitlementMemberResolution, AccountClassification | 3 |
| RecordViewed | 1 |
| A type added in a later version | 5 |

Recommended: forward everything and filter in the SIEM, so nothing is missing when you need it. Settings changes on the Admin pages are recorded as `FieldEdit`, like data edits; raise FieldEdit's severity if your SIEM should treat them as more important. `RecordViewed` events exist only when **Log read/view events** is on (Global Application Configuration).

### Status and testing

The **Status** section shows whether forwarding is on, when an event was last sent, how many have been sent, how many are waiting and since when, the last error, and the audit event forwarding started after (times in UTC).

**Send Test Event** sends one test event (event type `SiemForwardingTest`, severity 1) with the **saved** settings, whether forwarding is on or off, and reports success or the error. Save first, test, then turn forwarding on. Each test is recorded in the Audit Log as a `ConnectionTest` event; every save as a `FieldEdit` on `siem_forwarding_config`.

The **SIEM Forwarding** health check on **Admin > Deployment** is Degraded while forwarding is on and the last send failed, or the oldest waiting event is more than 15 minutes old. See the Operations Guide, "Monitoring SIEM forwarding".

## 9. API Keys

**Admin > API Keys** (permission `ManageApiKeys`, held by Admin only). An API key lets a script or another system call BlueTrack's API without a person signing in. The system sends its key to `POST /api/auth/token` and gets back a token that lasts 10 minutes; it then calls the API with `Authorization: Bearer <token>`, and asks for a new token when that one is about to expire. The key is only ever sent to that one address.

The key acts as the **role** you give it, and can do only what that role can do. The Admin role can't be given to a key, and a key never gets the permissions that manage API keys, sign-in (Identity Providers, Group → Role Mapping, Roles & Permissions), the Secrets Store, credentials, backups, Global Application Configuration or Reload Rights, whatever its role holds. A script that needs those should sign in as a person.

### Creating, disabling and deleting keys

**New API Key** asks for a **Name** (what the key is for), a **Role** and a **Lifetime** in days (the default is set on Global Application Configuration; 365 is the longest). After **Create API Key** the page shows the key once, with **Copy** (if the browser won't copy it, the page says so: select the key and copy it by hand); leaving the page before copying (other than with **Done**) asks you to confirm. Copy it then and store it where the script can read it (for example in the CyberArk vault). BlueTrack keeps only a one-way hash, so a lost key can't be shown again: delete it and create another.

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Name | Identifies the key in the list and the Audit Log. Must be unique. | Blank | The system or script that uses it | One key per caller means one key can be switched off without stopping the others. |
| Role | What the key may do. Every role except Admin is offered. | The first role listed | The narrowest role that does the job, for example Auditor or Viewer for a read-only report | A leaked key can do no more than its role. |
| Lifetime (days) | When the key expires. Allowed: 1 up to 365. | 90, or the Global Application Configuration setting | 90 | A forgotten key stops working by itself. |
| Owner email (optional) | Who is emailed before the key expires and on the day it does. | Blank | The person or team that owns the script | An expiring key then reaches the people who can replace it, not only the administrators. |
| Requests per minute (optional) | This key's own request limit. Blank uses the limit on Global Application Configuration. | Blank | Blank, unless one caller needs more or less | One busy caller can be given a higher limit, or a risky one a lower limit, without changing the others. |

The list shows each key's name, the **first characters** of the key (to tell keys apart, never enough to use it), role, expiry, status (**Enabled**, **Disabled** or **Expired**), when and from which address it was last used, and who created it. **Disable** stops a key at once, within about a minute (a token it already holds stops working too) and can be undone with **Enable**; **Delete** removes it for good after a confirmation. An expired key can't get a token.

### API client mappings (sign in with an identity provider's token)

A script can instead sign in with a token from an **OIDC identity provider** set up under **Identity Providers** (for example a client-credentials token from authentik or Entra ID). It sends that token as `idToken` to `POST /api/auth/token` (the same address as an API key, one or the other, never both) and gets the same 10-minute API token. BlueTrack checks the token against the provider's own published keys and issuer, only accepts asymmetric signatures, and rejects an expired or altered token. The token's audience isn't checked, because a script's own application is the audience, not BlueTrack: what the caller may do comes only from a mapping.

**Admin > API Keys > API client mappings** says which role such a caller gets. A mapping matches either the token's **client** (its `azp` or `client_id` claim) or a **group** (a value in the claim named as the *Groups claim type* on the identity provider, `groups` by default). These are separate from **Group → Role Mapping**, which is for people signing in: a script gets only what is mapped here. A mapping's role can be any role except Admin, and the same permissions that no API key can have are never given.

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Identity provider | The enabled OIDC provider whose tokens the mapping applies to. | The first one | The provider your scripts' applications are registered in | Tokens from a provider you haven't mapped, or a provider that is disabled, are refused. |
| Match by | **Client** (the token's client ID) or **Group** (a value in its groups claim). | Client | Client for a script's own application | A group match accepts any valid token from the provider that carries the group, including one issued to a person for another application. |
| Client ID or group | The value to match, exactly (case-sensitive). | Blank | The script application's client ID | Narrow matches keep the role to the caller you meant. |
| Role | What the caller may do. Every role except Admin is offered. | The first role listed | The narrowest role that does the job | A leaked token can do no more than its role. |
| Order | When more than one mapping matches a token, the lowest order is used (a client match before a group match when the order is equal). | 100 | Leave at 100 unless two mappings can match | Makes the result predictable. |
| Name | A label for the list. | Blank | What the caller is | For people reading the list. |

**Disable** or **Delete** a mapping to stop its callers: new exchanges are refused, and a token it already gave stops working within about a minute. A caller's actions in the Audit Log are attributed to "API client: <name>" (the token's `preferred_username`, else its client ID, else its subject). Each exchange (`ApiTokenIssued`) and each refusal (`ApiTokenRejected`) is audited, and each mapping change is `ApiClientMappingChanged`. The refusal throttle and the per-key rate limit apply to these callers too, the rate limit per caller. SAML identity providers can't be used for this.

### Audit and limits

Creating, disabling, enabling and deleting a key are in the Audit Log (`ApiKeyCreated`, `ApiKeyChanged`), as is every token a key is given (`ApiTokenIssued`) and every refused request (`ApiTokenRejected`, showing only the first characters of the key sent). Actions a key takes are attributed to the key (shown as "API key: <name>"). A refused request never says why (a wrong, disabled or expired key all look the same). After 10 refused requests in a minute from one address, further requests from it are answered `429` for a minute.

**Rate limit.** One key may make at most **API requests per minute, per key** requests a minute (Global Application Configuration, 600 by default), or its own **Requests per minute** if it has one. Over that, the API answers `429` with a `Retry-After` header.

**Editing a key.** Click a key's name to change its name, **Role**, **Expires on** (renew the key or shorten it, up to 365 days from today), **Owner email** and **Requests per minute**. The key itself is not changed, so the script keeps working; a role change applies at once, even to a token the key was given a minute ago. Every change is in the Audit Log with what it was and what it became.

**Rotate** on the list, after a confirmation, creates a new key with the same role, owner and limit (named "<name> (rotated <date>)" and lasting the default lifetime) and shows it once. The old key keeps working, so the script can be switched over without a gap; **Disable** or **Delete** the old key afterwards.

**Expiry emails.** The **ApiKeyExpiring** notification (**Admin > Notifications**, off until you enable it) emails a key's **owner** the **API key expiry reminder lead time** (Global Application Configuration, 7 days by default, 1 to 90) before the key expires, and again on the day it expires. A key with no owner email is skipped, and so is the reminder for a key made for no more than the lead time. The **ApiKeyExpired** notification (also off until enabled) emails the administrators, who are its recipients as for any notification type, on the day a key expires. Both go out from the daily digest time, once per key and expiry date; renewing a key's expiry earns new emails. See section 7.

**Expired keys are deleted automatically** the **Delete expired API keys after (days)** setting after they expire (Global Application Configuration, 365 by default; 0 keeps them for ever). The check runs every six hours; each deletion is in the Audit Log (`ApiKeyChanged`, "deleted automatically", by "BlueTrack API access (system)"). A key that is only disabled stays until you delete it.

Each request also checks that the key is still enabled and unexpired, so changes take effect quickly. A key's role is looked up on every request.
