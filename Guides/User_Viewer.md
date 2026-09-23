# User Guide: Viewer

**Blueprint Progress Tracking Web Interface**

## Who this is for

Someone whose BlueTrack role grants only the `ViewDashboard` permission — read-only visibility into overall progress, with no editing rights anywhere in the app. This is the shortest of the role guides; if your role turns out to include more than this, see [User_Analyst.md](User_Analyst.md) or [User_Approver.md](User_Approver.md) instead.

## Signing in

Use whichever identity provider your organization has configured (Windows Integrated, or a real IdP if one's set up) — see your organization's own sign-in instructions if you're not sure which.

## The Dashboard

**Where**: the home page you land on after signing in (also reachable any time via **Dashboard** in the top nav).

This is the one page a Viewer role is built around. Four at-a-glance cards, each linking into more detail:

- **Accounts by Stage** — a table plus a pie chart of how many accounts sit at each Blueprint stage.
- **Key Progress Indicators** — the four KPI ratios (In Scope vs. All Accounts, Onboarded vs. In Scope, Managed vs. Onboarded, Compliant vs. Managed) as percentages, plus a summary pie chart.
- **Overdue / At-Risk Accounts** — a count of accounts past their target remediation date.
- **Risk Exceptions Needing Attention** — a count of exceptions past their review date.

Click any card to drill into the full report or worklist behind it.

## Your profile

**Where**: the user menu (`/profile`).

Shows your display name, mapped role, and permission list. Click **Reload My Rights** if you've just been added to a new group and don't want to wait for it to take effect on its own. Also where you pick a display theme: Light, Dark, or High Visibility.

## See also

- `Design Documents/Design_User_Guide.md` — the full reference guide, covering every page in the application, not just the ones a Viewer role reaches.
