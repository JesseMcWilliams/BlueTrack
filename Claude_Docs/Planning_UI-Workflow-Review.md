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
7. **Accessibility basics** (worklist row links and named Delete buttons shipped, D-257 and D-258): toggles without `aria-expanded` and forms that open away from the control without moving focus are done. (W5-5, W5-6, W9-9 and W10-8 shipped in D-262 to D-264)

## Workflow 1: Sign-in and access

Today: the router guard sends an unauthenticated user to `/login`; Login lists or auto-starts the methods; after sign-in the user lands on `returnUrl`; a user with no roles still signs in and gets the whole menu, and only My Profile says "Roles: none".

| ID | Finding | Effort |
|---|---|---|

Works well: distinct idle and signed-out messages, an open-redirect-safe `returnUrl`, group names resolved with a Lookup tool that explains effective rights.

## Workflow 2: Tracking migration progress

Today: Dashboard, then the Accounts list (seven filters, multi-sort, paging), then account detail (edit lock taken at once, tabbed form, Save returns to the list), or bulk edit (select, tick fields, Apply).

| ID | Finding | Effort |
|---|---|---|
| W2-6 | Bulk Apply has no result summary on the shared bulk components; a ticked Date or text field left empty clears that field on every selected account (the confirmation now lists the fields). | S |

Works well: selection survives paging and sorting; sort headers are real buttons with `aria-sort`; a regression needs a reason; delete and undelete keep a visible history.

## Workflow 3: Risk exceptions

Today: the Dashboard links to the two worklists (the list page doesn't); create picks Account or Application, with a raw AccountKey for Account; the detail has Extend and Revoke; the approval worklist lists every Active exception.

| ID | Finding | Effort |
|---|---|---|
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
| W5-4 | Dismiss on Possible Matches and Discovered Accounts has no undo or "show dismissed" view (it now confirms). | M |

Works well: Link's confirm names both accounts; Unresolved Members pre-fills the exact match and offers Undo; Account Classification reports how many accounts it changed.

## Workflow 6: Decommissioning

Read-only reports; the admin acts in CyberArk. Rows link to the account detail. Every finding for this workflow has shipped (D-262, D-267).

## Workflow 7: Reporting

Every finding for this workflow has shipped (D-261, D-263, D-267).

Works well: exports respect the active filters and sort and relay the API's error; Trends has chart text alternatives and a data table behind each chart.

## Workflow 8: Application mapping

| ID | Finding | Effort |
|---|---|---|
| W8-3 | Bulk imports write at once with no preview (a dry run needs a validate-only mode in the import API); the rest of W8-3 shipped in D-266. | M |

## Workflow 9: Data loading and operations

| ID | Finding | Effort |
|---|---|---|

Works well: the business-hours guard on Run now, Test on unsaved feed values, Global Application Configuration's save feedback and pattern tester.

## Workflow 10: Security setup

| ID | Finding | Effort |
|---|---|---|

Works well: the API key reveal (shown once, select on focus, Copy), the keys list (status, last used, where from), SIEM status and "Test first" hint.

## Workflow 11: Audit and fields

| ID | Finding | Effort |
|---|---|---|

## Workflow 12: Personal settings

| ID | Finding | Effort |
|---|---|---|

Works well: the user menu is a proper disclosure (Escape returns focus, closes on route change), the About dialog is modal with focus handled, the idle timer is shared across tabs.

## Suggested order

1. ~~Quick, high-value fixes: the bugs and the missing confirmations.~~ Shipped (D-251). The last three confirmations shipped in D-256.
2. ~~List state in the URL and drill-down.~~ Shipped (D-252, D-258, D-259, D-261).
3. ~~One shared error helper, empty states and "saved" messages.~~ Shipped (D-253). Still open from that group: see themes 1 and 5.
4. ~~No-access experience (W1-1, W1-2).~~ Shipped (D-254, D-255). Still open: pages without a route-level permission still show "Request failed: 403" to a user who holds some roles but not that page's permission.
5. The rest by workflow.
