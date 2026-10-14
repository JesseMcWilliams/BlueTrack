# User Guide: Viewer

**Blueprint Progress Tracking Web Interface**

## Who this is for

A read-only role: `ViewDashboard` only, no editing anywhere in the app.

## Signing in

Open BlueTrack and sign in with the method your organization has set up: Windows signs you in silently, and OIDC or SAML takes you to your identity provider and back. If the page lists several methods, pick yours (for Windows, choose **Continue**). Your administrator may also give you a direct sign-in link. See `User_Guide.md`, "Signing in".

## The Dashboard

**Where**: the home page after signing in (also **Dashboard** in the top nav).

Four cards, each linking into more detail:

- **Accounts by Stage** — a table plus a pie chart of accounts per Blueprint stage.
- **Key Progress Indicators** — the four KPI ratios as percentages, plus a summary pie chart.
- **Overdue / At-Risk Accounts** — a count of accounts past their target remediation date.
- **Risk Exceptions Needing Attention** — a count of exceptions past their review date.

Click any card to open the full report or worklist behind it.

## Reports

**Where**: **Reports** in the top nav. The reports are listed in a column on the left. **Trends** shows accounts by stage, % complete and accounts by risk level over time, one point per night, filtered by date range, Day or Week, Application and Safe.

Every report you can view has **Export CSV** and **Export Excel**. The file holds all rows matching the report's current filters and sort, not just the page shown, up to the administrator's **Export Max Rows** limit (a larger export is refused with the row count). Each export is recorded in the Audit Log. See `User_Guide.md`, Reports.

## Your profile

**Where**: open the menu with your name at the right of the top nav and choose **Profile & Preferences** (`/profile`).

Shows your display name, role, and permission list. Click **Reload My Rights** if you've just been added to a new group (otherwise it can take up to 15 minutes). Pick a display theme (Light, Dark, High Visibility) and a **Language** here too (English for now; your browser's language until you choose). Counts and dates use your browser's format.

## See also

- `User_Guide.md` — the full reference guide for every page in the application.
