# BlueTrack: Calling the API from a script

**For BlueTrack administrators and the people who write the scripts**

A script or another system can call BlueTrack's API without a person signing in. It proves who it is with an **API key**, or with a token from your **OIDC identity provider**, and swaps that for a **BlueTrack token** that lasts 10 minutes. The token is what goes on API calls. This guide shows the steps; every setting is in [Admin_SettingsReference_Notifications.md](Admin_SettingsReference_Notifications.md), section 9.

In the examples, `https://bluetrack.example.com/BlueTrack` stands for your BlueTrack address (the site, then the API's path, as the installer set it up). `<your API key>` is a placeholder; never put a real key in a script file that is shared or committed. Keep it where the script can read it and others can't, for example in the CyberArk vault.

## What a caller can do

- It acts as **one role** you choose, and can do only what that role can do in BlueTrack (view reports, export, and so on). Pick the narrowest role that does the job.
- It can never be given the **Admin** role, and never the permissions that manage API keys, sign-in, roles and group mapping, the Secrets Store, credentials, backups, Global Application Configuration or Reload Rights.
- There is no separate, versioned API for scripts: the calls are the ones BlueTrack's own pages make, and they can change between releases. Check a script after each upgrade.

## 1. With an API key

**Create the key.** An administrator opens **Admin > API Keys** and chooses **New API Key**: a name (what the key is for), a role, and a lifetime (90 days by default). The page shows the key once. Copy it then; BlueTrack keeps only a one-way hash, so a lost key can't be shown again.

**Get a token.** Send the key to the token address (this is the only time the key is sent):

```powershell
$base = 'https://bluetrack.example.com/BlueTrack/api'
$body = @{ apiKey = '<your API key>' } | ConvertTo-Json
$token = (Invoke-RestMethod "$base/auth/token" -Method Post -Body $body -ContentType 'application/json').access_token
```

```bash
curl -s -X POST https://bluetrack.example.com/BlueTrack/api/auth/token \
  -H 'Content-Type: application/json' -d '{"apiKey":"<your API key>"}'
```

The answer is `{ "access_token": "...", "token_type": "Bearer", "expires_in": 600 }`.

## 2. With a token from your identity provider

Instead of an API key, a script can use a token its own application gets from an **OIDC identity provider** that is set up and enabled under **Identity Providers**. Get the token the way your provider describes (for example a client-credentials request with the script application's client ID and secret), then send it as `idToken` instead of `apiKey`:

```powershell
$body = @{ idToken = $idpToken } | ConvertTo-Json
$token = (Invoke-RestMethod "$base/auth/token" -Method Post -Body $body -ContentType 'application/json').access_token
```

BlueTrack accepts the token only if the provider signed it, it hasn't expired, and an **API client mapping** (**Admin > API Keys**, below the keys) gives it a role: by the token's client ID, or by a group in the token. A token with no mapping is refused. Send exactly one of `apiKey` and `idToken`.

## 3. Call the API with the token

```powershell
Invoke-RestMethod "$base/me" -Headers @{ Authorization = "Bearer $token" }
```

`/api/me` shows who the token is and what its role can do, which is a good first check. A call that changes data needs no extra header when it carries the bearer token.

**Tokens last 10 minutes.** A long-running script asks for a new token (the same way) when it gets `401`, or a little before the 10 minutes are up. Keep the key or the provider credentials, not the token.

## What the answers mean

| Answer | Meaning |
|---|---|
| `200`, `201`, `204` | It worked. |
| `401` on the token address | The key or token wasn't accepted. It never says why: the key may be wrong, disabled or expired, the provider token expired or unmapped. Check **Admin > API Keys** and the Audit Log (`ApiTokenRejected`). |
| `401` on another call | The token has expired (ask for a new one), or its key or mapping was disabled, deleted or changed. |
| `403` | The token's role isn't allowed to do that. Use a role that is, or a different approach if it needs a permission an API caller can never have. |
| `429` | Too many requests: more than the per-key limit in a minute (600 by default), or more than 10 refused token requests in a minute from one address. Wait for the time in the `Retry-After` header. |

## Keeping keys safe

- **One key per script or system**, so one can be disabled without stopping the others.
- **Rotate** before a key expires: **Rotate** on the **Admin > API Keys** list makes a new key with the same role and shows it once. Switch the script over, then **Disable** or **Delete** the old key. If you turn on the **ApiKeyExpiring** notification (**Admin > Notifications**), recipients are emailed 30 and 7 days before a key expires.
- **Disable** a key you suspect has leaked. It stops within about a minute, even for a token it already gave.
- Every token request and every action a caller takes is in the **Audit Log**, attributed to "API key: <name>" or "API client: <name>".
