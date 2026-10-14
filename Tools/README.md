# Tools: checks against a real BlueTrack install

PowerShell scripts that check a running BlueTrack, its sign-in, its directory and its integrations from the command line (D-219). They run on Windows PowerShell 5.1 or PowerShell 7, change nothing (one noted exception), and keep passwords in memory only.

Each prints one line per check, `PASS`, `FAIL`, `WARN` (worth a look) or `INFO` (a fact, or something it can't check), then the totals. The exit code is 1 if any check failed, so a script can be scheduled or used in a pipeline. Counts are shown in the current culture's number format.

| Script | Run it | What it checks |
|---|---|---|
| `Test-BlueTrackHealth.ps1` | On the BlueTrack server, as an Admin | Windows services (IIS, SQL Server, SQL Server Agent, a CI runner if present), the web pages, the version (About), your Windows sign-in, every Deployment page health check, and the nightly SQL Agent jobs' last runs. |
| `Test-BlueTrackSso.ps1` | Anywhere that reaches the site and the IdP, with a test account | SAML and/or OIDC sign-in without a browser, the user's roles, Sign out (SAML Single Logout when set up) and that the session ended. Written for authentik (see below). |
| `Test-BlueTrackLdaps.ps1` | On the host that will bind, before setting up the domain in BlueTrack | DNS, the LDAPS port, the domain controller's certificate (trusted, right name, not expired), a bind made the way BlueTrack makes it, and reading a group. |
| `Test-BlueTrackIntegrations.ps1` | As an Admin | BlueTrack's own Test buttons: the Secrets Store lookup, the CyberArk sources, every data feed; and the break-glass setup (accounts, alert recipients, recent BreakGlassLogon events). |

`BlueTrackTools.psm1` holds the shared parts (result lines, an HTTP session that follows redirects itself and keeps its own cookies).

## Examples

```powershell
# On the server, after an install or an update
.\Test-BlueTrackHealth.ps1 -SiteUrl https://bluetrack.company.com

# SAML and OIDC with a test account (prompts for the password)
.\Test-BlueTrackSso.ps1 -SiteUrl https://bluetrack.company.com -Credential (Get-Credential)

# The same, with the account's password from the CyberArk Credential Provider
.\Test-BlueTrackSso.ps1 -SiteUrl https://bluetrack.company.com -CpAppId '<app id>' -CpSafe '<safe>' -CpObject '<object>'

# From another domain's server, before adding that domain to BlueTrack
.\Test-BlueTrackLdaps.ps1 -DomainController dc01.example.com -SearchBase 'DC=example,DC=com' -Credential (Get-Credential EXAMPLE\svc-bluetrack-ldap)

# The Test buttons, including one Secrets Store lookup
.\Test-BlueTrackIntegrations.ps1 -SiteUrl https://bluetrack.company.com -SecretSafe '<safe>' -SecretObject '<object>'
```

Every script has full help: `Get-Help .\Test-BlueTrackSso.ps1 -Full`. Add `-Verbose` to `Test-BlueTrackSso.ps1` to see each redirect and sign-in step (secrets in addresses are hidden).

## Limits

- **`Test-BlueTrackSso.ps1` speaks authentik's flow API.** It answers the identification, password, consent and auto-submit steps; an account with MFA or a captcha stops it with the step's name. Another identity provider needs its own version, or a browser.
- **Without a browser, two Single Logout legs can't be checked**: the identity provider's LogoutResponse back to `/api/auth/saml/slo`, and a logout started at the identity provider. Both are front-channel (the browser carries them); the script says so as `INFO`.
- **The sign-ins are real**: they're audited as Logon events, and the account's BlueTrack user is created the first time. Use a test account.
- **A Secrets Store lookup is real too**: BlueTrack records it in the Audit Log as a ConnectionTest event. The secret itself is never shown.
- **An LDAPS bind from a domain controller to itself doesn't answer**; the script gives up after 30 seconds and says so. Run it from another host.
- The API calls use Windows authentication as the account running the script, so it needs the matching permissions; a check it can't make for lack of one is a `WARN`, not a failure.

Scripts here must pass PSScriptAnalyzer with `Deploy\PSScriptAnalyzerSettings.psd1`, like `Deploy/`.
