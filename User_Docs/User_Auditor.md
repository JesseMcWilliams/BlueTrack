# User Guide: Auditor

**Blueprint Progress Tracking Web Interface**

## Who this is for

`ViewAuditLog` only — read-only visibility into the audit trail, with no editing rights anywhere in the app.

## Signing in

Open BlueTrack and sign in with the method your organization has set up: Windows signs you in silently, and OIDC or SAML takes you to your identity provider and back. If the page lists several methods, pick yours (for Windows, choose **Continue**). Your administrator may also give you a direct sign-in link. See `User_Guide.md`, "Signing in".

## The Audit Log Viewer

**Where**: **Admin > Audit Log Viewer**.

Read-only — no add/edit/delete. Filter by Event Type, Entity, and a From/To date range, then click **Filter**. The list shows Occurred At, Event Type, By, Entity, and Detail. Click **Occurred At** on any row to expand the recorded Reason (if any) and a Field / Old Value / New Value table.

**Export CSV** and **Export Excel** download every event matching the filters you last applied with **Filter**, in the list's sort, with the same five columns. **Occurred At** keeps the time, in UTC (headed **Occurred At (UTC)**). The Reason and field changes aren't in the file; open the event in the viewer for those. More events than the administrator's **Export Max Rows** limit are refused with the count: narrow the dates first.

Every export, of a report or of the Audit Log, is recorded as a `ReportExport` event, with who exported what (the report, or `audit-log`), the format, the number of rows and the filters used.

Your organization may also forward the audit log to its SIEM (**Admin > SIEM Forwarding**, set by an administrator). Each event then appears there too, with its audit event number as `externalId`. Changes to the forwarding settings are recorded as `FieldEdit` events on `siem_forwarding_config`, and each **Send Test Event** as a `ConnectionTest` event.

## The Dashboard and tracked data

The Dashboard, **Accounts**, **Exceptions** and most **Reports** need `ViewDashboard`, which this role doesn't include. You still see those entries in the top nav, but their pages show an error (such as "Request failed: 403") instead of data. If you need them, ask an administrator to add a role with `ViewDashboard` (for example Viewer) to your group.

## Your profile

**Where**: open the menu with your name at the right of the top nav and choose **Profile & Preferences** (`/profile`). Shows your display name, role, and permission list, and lets you pick a display theme and a **Language** (English for now; your browser's language until you choose).

## See also

- `User_Guide.md` — the full reference guide, covering every page in the application.
