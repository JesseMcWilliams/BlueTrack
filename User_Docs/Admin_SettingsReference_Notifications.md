# BlueTrack Settings Reference: Secrets, Credentials and Notifications

**For BlueTrack administrators**

This is the second part of the settings reference. The first part, [Admin_SettingsReference.md](Admin_SettingsReference.md), explains how to read the tables and covers Global Application Configuration, sign-in and access, data and risk scoring, the Deployment page and the installer. This part covers the Secrets Store, the credentials BlueTrack signs in with, LDAP and email notifications. Both parts are published together as the Administrator Settings Guide.

## 6. Secrets Store and Credentials

### Secrets Store Configuration

**Admin > Secrets Store Configuration** (permission `ManageSecretsStore`). Exactly one backend is active at a time; Windows DPAPI is active on a new install. Click **Make Active** to switch, or **Save** on the active backend. **Test Connection** fetches one secret from the active backend and reports success with the user name and address, never the secret itself. Each test is recorded in the Audit Log as a `ConnectionTest` event, as is each credential **Test** on the Credentials & LDAP page.

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

**Admin > Credentials & LDAP** (permission `ManageCredentials`). Named credentials that BlueTrack itself signs in with: the SMTP account (or, for OAuth2 email sign-in, the client secret, certificate or key) and LDAP bind accounts.

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Name | The credential's name, chosen on the SMTP and LDAP settings. | Blank | Say what it is for, for example "SMTP relay account" | Clear names avoid using the wrong account. |
| Backend | Where the password is kept: WindowsDpapi (on this server) or one of the vault backends. | WindowsDpapi | A vault backend if you have one | The password is then managed and rotated in the vault. |
| Username and Password (WindowsDpapi) | The account and its password, encrypted on this server. Leave the password blank to keep it. For an OAuth2 email credential, only the password is used (see "OAuth2 email sign-in"). | Blank | A dedicated service account | A dedicated account limits what a leaked password can do. |
| DPAPI Scope (WindowsDpapi) | Machine: any process on this server can decrypt it. User: only the App Pool's account can. | Machine | User | User scope keeps other processes on the server, and anyone holding a database backup, from decrypting it. |
| Safe, Folder, Object / Secret Name (vault backends) | Where the credential is found in the vault, using that backend's settings from the Secrets Store page. | Blank | As named in your vault | BlueTrack fetches the current password each time it's used. |

A new User-scope credential is stored at Machine scope until the application decrypts it once, then upgrades itself. As with any DPAPI secret, a credential can't be decrypted on a different server, and a User-scope one can't be decrypted after the App Pool's account changes: re-enter the password in those cases. Use **Test** to check a credential.

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
| DevFakeAuthEnabledTooLong | When DevFakeAuth has been enabled for more than 7 days; repeats at most every 7 days. | How long DevFakeAuth has been enabled, and since when. | On |
| BreakGlassLogon | At once, every time a designated break-glass account signs in (Admin_SettingsReference.md, Identity Providers, "Break-glass accounts"). | The account, the sign-in method, the source IP address and the time (UTC), and where to find it in the Audit Log. | On |

Recommended: enable NightlyImportFailed on every real environment, since nothing else in BlueTrack tells you a nightly import failed. Set every type's target role to your Admin role, or to a role whose Notification Email is a monitored team mailbox. NightlyImportFailed doesn't cover a job that never starts at all (SQL Server Agent stopped, or the job disabled); `Tools\Test-BlueTrackHealth.ps1` checks those.

**Emails** have a subject starting `BlueTrack:`, and an HTML and a plain-text version with the same content. When **Site URL** is set on Global Application Configuration, they link to the pages they mention (for example Overdue Reviews, Trends, KPI Summary, CyberArk Sources, Audit Log); without it they name the pages.

### Schedule

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Daily digest time | When the daily emails (OverdueRiskExceptions) go out, server local time. | 07:00 | Before your team's working day starts | The list is waiting when people arrive. If the server is down at that time, the email goes out when it's back the same day. |
| Weekly summary day | The day WeeklySummary goes out. | Mon | The first working day of your week | Starts the week with last week's progress. |
| Weekly summary time | The time it goes out that day, server local time. | 07:00 | Same as the daily digest | One time is easier to remember. |

Click **Save Schedule** to save the schedule; it is saved separately from the SMTP settings, and every change is recorded in the audit log. Each scheduled email goes out once per day or week, even if the API restarts.

### Recipients and testing

**Recipients.** Each recipient has an **Email**, an optional **Display Name** and an **Active** flag. Add at least one monitored mailbox, or give each type a target role; with neither, alerts go nowhere.

**Send Test Email** sends a real message to every active recipient using the saved settings. Use it after every SMTP change. Each test is recorded in the Audit Log as a `ConnectionTest` event, with the authentication method and whether it worked. A failed OAuth2 test says what went wrong, for example "Token endpoint returned invalid_client".
