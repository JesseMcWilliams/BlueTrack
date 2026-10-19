# BlueTrack: UI Workflow Review

> **Stage: Planning.** A review of twelve user workflows, requested 2026-10-19, done by reading the Vue pages, their strings and the API calls they make (the app wasn't run, so visual layout isn't judged). Four read-only reviewers took the workflows in order; the claims below that decide priorities were then re-checked against the code by the author, and the rest carry the reviewers' file and line evidence. "Unverified" means the reviewer couldn't confirm it from the code. Step 1 of the suggested order (the bugs and the missing confirmations) shipped as D-251 and has been deleted from the lists below; the rest is not built. Each finding has an ID (`W<workflow>-<n>`) so work can be picked by ID. Effort: S under half a day, M a day or two, L more.

The Self-Hosted Direct setup workflow (Credentials, CyberArk sources, Data Sources) was reviewed and improved earlier the same day (D-249, D-250) and isn't repeated.

## Themes that cut across the workflows

1. **A failed save says only a status code: shipped (D-251, D-253)** for the edit pages. Still open: Credentials LDAP save, Notifications and Account Detail's other-status branch still show their own status text. (W9-5)
2. **Destructive or live-changing buttons that don't confirm**, next to ones that do. Shipped (D-251, D-256): Revoke, Ignore, Force Release, Make active, Dismiss, Rotate and Bulk Apply. Still open: no undo or "show dismissed" view (W5-4).
3. **"Saved" message after a save that navigates away: shipped (D-253)** on every edit page that returns to a list and on Account Detail.
4. **List state in the URL and drill-down: shipped (D-252)** for the Accounts list, Trends, Discovered Accounts and Risk Score, and for the Dashboard and stage-by-status counts. The KPI counts shipped in D-261.
5. **Missing empty states: shipped (D-253)** for Targets, Access Groups, Accounts, Discovered Accounts and the admin lists. Still open: Risk Score report (a report, not the Bands list).
6. **Silent failures**: bulk-import HTTP errors on Application mapping render nothing (Targets and Access Groups shipped in D-251); theme, language and page-size saves ignore failures. (W8-3, W12-3)
7. **Accessibility basics** (worklist row links and named Delete buttons shipped, D-257 and D-258): toggles without `aria-expanded`, forms that open away from the control without moving focus. (W3-5, W4-7, W5-5, W5-6, W9-9, W10-8)

## Workflow 1: Sign-in and access

Today: the router guard sends an unauthenticated user to `/login`; Login lists or auto-starts the methods; after sign-in the user lands on `returnUrl`; a user with no roles still signs in and gets the whole menu, and only My Profile says "Roles: none".

| ID | Finding | Effort |
|---|---|---|
| W1-5 | The Identity Provider pages never say OIDC changes need an app restart (a code comment only). Unverified: wording elsewhere. | S |
| W1-7 | No lock-out warning when deleting the default or last enabled provider, or your own role. Unverified: the server may refuse. | M |

Works well: distinct idle and signed-out messages, an open-redirect-safe `returnUrl`, group names resolved with a Lookup tool that explains effective rights.

## Workflow 2: Tracking migration progress

Today: Dashboard, then the Accounts list (seven filters, multi-sort, paging), then account detail (edit lock taken at once, tabbed form, Save returns to the list), or bulk edit (select, tick fields, Apply).

| ID | Finding | Effort |
|---|---|---|
| W2-5 | A view-only user isn't told why there is no edit form (`:542,651`). | S |
| W2-6 | Bulk Apply has no result summary on the shared bulk components; a ticked Date or text field left empty clears that field on every selected account (the confirmation now lists the fields). | S |
| W2-8 | No confirmation after Save; the risk override saves on its own, unexplained (Cancel won't undo it). | S |
| W2-9 | A reference-data load failure in bulk edit is silent (empty dropdowns). | S |

Works well: selection survives paging and sorting; sort headers are real buttons with `aria-sort`; a regression needs a reason; delete and undelete keep a visible history.

## Workflow 3: Risk exceptions

Today: the Dashboard links to the two worklists (the list page doesn't); create picks Account or Application, with a raw AccountKey for Account; the detail has Extend and Revoke; the approval worklist lists every Active exception.

| ID | Finding | Effort |
|---|---|---|
| W3-4 | An application-scoped exception doesn't list the accounts it covers (the detail now names the account or application, D-259). | M |
| W3-6 | Neither worklist shows the linked account, and neither can extend or revoke inline (they are now linked from the exceptions list). | S |
| W3-7 | "Approval worklist" lists every Active exception; there is no pending-approval state. Unverified whether the API has one. Rename or confirm the purpose. | S |
| W3-8 | Expired and Revoked exceptions show no reason, who or when (D-190 records it). | S |

Works well: bulk import (template, rules, summary with row errors and new IDs), the shared list pattern with bulk extend and revoke and a required reason, links to the source ticket.

## Workflow 4: Targets and access groups

Today: list with type, application and linked filters; Target Edit with identifiers and Link to another target; bulk import (inventory, account-to-target); Admin > Target Match Review (Link, New Target, Ignore); access groups with the same pattern and three import CSVs.

| ID | Finding | Effort |
|---|---|---|
| W4-5 | No path from an import's "N pending review" to the queue, which is only under Admin. | S |

Works well: link and unlink confirmations that say what will happen, the delete confirmation that names the target, a readable 409 for a duplicate access group, column checks before an import.

## Workflow 5: Reconciliation and discovery

| ID | Finding | Effort |
|---|---|---|
| W5-2 | Discovered Accounts has no empty state. | S |
| W5-3 | Reconciliation Review is a dead end: no hint, read-only ("actions aren't wired up yet" in its own comment), and nothing links it to Possible Matches, which does the linking. Add a hint and link, or retire it if superseded (unverified). | S |
| W5-4 | Dismiss on Possible Matches and Discovered Accounts has no undo or "show dismissed" view (it now confirms). | M |
| W5-5 | The Resolve form on Unresolved Members opens at the top of the page, away from the row, with no focus move and no message on save. | S |
| W5-6 | The account-name toggle has no `aria-expanded`; the Classify form opens without moving focus. | S |
| W5-7 | The "possible match" note names the account without linking to it; Account Classification doesn't link to Discovered Accounts, where its effect shows. | S |

Works well: Link's confirm names both accounts; Unresolved Members pre-fills the exact match and offers Undo; Account Classification reports how many accounts it changed.

## Workflow 6: Decommissioning

Read-only reports; the admin acts in CyberArk. Rows link to the account detail.

| ID | Finding | Effort |
|---|---|---|
| W6-1 | The Safes report can't show which accounts are in a flagged Safe; the Accounts report has no Safe filter. | M |
| W6-2 | The Accounts report loads every row, with no sorting, paging, search or count. | M |
| W6-3 | No "what next" line (delete in CyberArk; the report updates after the next load) and no reviewed state. | S |
| W6-4 | The Application name isn't linked to its edit page. | S |

## Workflow 7: Reporting

| ID | Finding | Effort |
|---|---|---|
| W7-3 | KPI Summary doesn't say what Onboarded, Managed or Compliant mean, or give an "as of" date (the definitions are in a code comment, D-143). | S |
| W7-4 | Overdue/At-Risk can't be sorted, filtered or paged, and shows no days overdue. | M |
| W7-5 | Trends doesn't validate the date range and refetches on every keystroke. | S |
| W7-6 | The Risk Score account name expands a panel instead of linking to the account. | S |
| W7-7 | The Risk Score report has no empty state (the Bands list does). | S |

Works well: exports respect the active filters and sort and relay the API's error; Trends has chart text alternatives and a data table behind each chart.

## Workflow 8: Application mapping

| ID | Finding | Effort |
|---|---|---|
| W8-2 | The Safes table can't be searched or filtered to unassigned only. | M |
| W8-3 | Bulk imports write at once with no preview; a network error is silent; row errors are one semicolon-joined string. | S |
| W8-4 | The Application edit page shows only a status code on a failed save (a 409 for a duplicate code); email fields aren't `type="email"`. | S |
| W8-5 | No way to delete an application and nothing shows how many Safes or accounts use it. Unverified: delete may exist elsewhere. | M |
| W8-6 | The account-to-application relationship is hard to find (Safe mapping, a CSV, the detail form, Bulk Edit). Add an intro with links. | S |
| W8-7 | Platform Mapping: Remove can be double-clicked; Save is silently disabled. | S |

## Workflow 9: Data loading and operations

| ID | Finding | Effort |
|---|---|---|
| W9-1 | Every Notifications action reloads and remounts the page, losing unsaved SMTP or schedule edits (`Notifications.vue:175-268`). | S |
| W9-3 | The SMTP Test email uses the saved settings but stays enabled while the form is dirty. | S |
| W9-4 | Failed runs, Unhealthy checks and failed backups aren't marked in the run history, health checks or backup table. | S |
| W9-5 | Add-recipient failure shows only the status code. | S |
| W9-7 | Backup App has no confirmation and no success line naming the file. | S |
| W9-8 | No "Saved" message after a feed or recipient save. | S |
| W9-9 | The run-history time is an `<a href="#">` toggle with no `aria-expanded`. | S |

Works well: the business-hours guard on Run now, Test on unsaved feed values, Global Application Configuration's save feedback and pattern tester.

## Workflow 10: Security setup

| ID | Finding | Effort |
|---|---|---|
| W10-2 | Identity Provider pages never say OIDC changes need an app restart. | S |
| W10-3 | No lock-out warning when deleting an identity provider. | S |
| W10-4 | No leave-page guard on the API key reveal screen (Rotate now confirms). | S |
| W10-5 | Copy failure is silent on plain-HTTP hosts (`ApiKeyReveal.vue`). | S |
| W10-7 | SIEM Save and Test messages appear at the top of a long form, away from the buttons. | S |
| W10-8 | Secrets Store credential inputs have no label; the Role permission list isn't a fieldset. | S |

Works well: the API key reveal (shown once, select on focus, Copy), the keys list (status, last used, where from), SIEM status and "Test first" hint.

## Workflow 11: Audit and fields

| ID | Finding | Effort |
|---|---|---|
| W11-1 | Event type is free text and there is no filter for who did it. Unverified: whether the API supports a user filter. | M |
| W11-2 | Field Metadata type and reference table are free text; a typo can break the Account Progress edit form. Use selects. | M |
| W11-4 | Audit timestamps are the raw stored string with no zone. | S |
| W11-5 | No Clear filters button. | S |

## Workflow 12: Personal settings

| ID | Finding | Effort |
|---|---|---|
| W12-1 | Edits typed in a form are lost when the idle sign-out happens (the warning now gives two minutes). Unverified: whether sign-in returns to the page. | M |
| W12-2 | Reload My Rights replaces the whole page with "Loading" and gives no confirmation; a failure replaces the page with an alert. | S |
| W12-3 | Theme, language and page-size saves ignore failures and show no "Saved". | S |
| W12-4 | Page size can be set only from list pagers, not on the profile page. | S |
| W12-5 | A sign-out error appears detached, under a menu that has already closed. Unverified how it looks. | S |

Works well: the user menu is a proper disclosure (Escape returns focus, closes on route change), the About dialog is modal with focus handled, the idle timer is shared across tabs.

## Suggested order

1. ~~Quick, high-value fixes: the bugs and the missing confirmations.~~ Shipped (D-251). The last three confirmations shipped in D-256.
2. ~~List state in the URL and drill-down.~~ Shipped (D-252, D-258, D-259, D-261).
3. ~~One shared error helper, empty states and "saved" messages.~~ Shipped (D-253). Still open from that group: see themes 1 and 5.
4. ~~No-access experience (W1-1, W1-2).~~ Shipped (D-254, D-255). Still open: pages without a route-level permission still show "Request failed: 403" to a user who holds some roles but not that page's permission.
5. The rest by workflow.
