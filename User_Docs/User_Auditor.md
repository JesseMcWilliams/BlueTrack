# User Guide: Auditor

**Blueprint Progress Tracking Web Interface**

## Who this is for

`ViewAuditLog` only — read-only visibility into the audit trail, with no editing rights anywhere in the app.

## Signing in

Open BlueTrack and sign in with the method your organization has set up: Windows signs you in silently, and OIDC or SAML takes you to your identity provider and back. If the page lists several methods, pick yours (for Windows, choose **Continue**). Your administrator may also give you a direct sign-in link. See `User_Guide.md`, "Signing in".

## The Audit Log Viewer

**Where**: **Admin > Audit Log Viewer**.

Read-only — no add/edit/delete. Filter by Event Type, Entity, and a From/To date range, then click **Filter**. The list shows Occurred At, Event Type, By, Entity, and Detail. Click **Occurred At** on any row to expand the recorded Reason (if any) and a Field / Old Value / New Value table.

## The Dashboard and tracked data

The Dashboard, **Accounts**, **Exceptions** and most **Reports** need `ViewDashboard`, which this role doesn't include. You still see those entries in the top nav, but their pages show an error (such as "Request failed: 403") instead of data. If you need them, ask an administrator to add a role with `ViewDashboard` (for example Viewer) to your group.

## Your profile

**Where**: **My Profile** at the right of the top nav (`/profile`). Shows your display name, role, and permission list, and lets you pick a display theme.

## See also

- `User_Guide.md` — the full reference guide, covering every page in the application.
