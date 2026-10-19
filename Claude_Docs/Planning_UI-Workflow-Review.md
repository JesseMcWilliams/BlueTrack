# BlueTrack: UI Workflow Review

> **Stage: Planning.** A review of twelve user workflows, requested 2026-10-19, done by reading the Vue pages, their strings and the API calls they make (the app wasn't run, so visual layout isn't judged). Four read-only reviewers took the workflows in order; the claims below that decide priorities were then re-checked against the code by the author, and the rest carry the reviewers' file and line evidence. "Unverified" means the reviewer couldn't confirm it from the code. Nothing here is built. Each finding has an ID (`W<workflow>-<n>`) so work can be picked by ID. Effort: S under half a day, M a day or two, L more.

The Self-Hosted Direct setup workflow (Credentials, CyberArk sources, Data Sources) was reviewed and improved earlier the same day (D-249, D-250) and isn't repeated.

## Themes that cut across the workflows

1. **A failed save loses the user's work or says only a status code.** `RiskExceptionEdit` hides the whole form when `error` is set; Identity Provider, Role, Application, Notification recipient and Credential saves show "save failed: 400" where other pages show the API's `problem.detail`. Fix once with a shared error-from-response helper. (W3-1, W8-4, W9-5, W10-9)
2. **Destructive or live-changing buttons that don't confirm**, next to ones that do: Revoke exception, Dismiss a possible match, Ignore in Target Match Review, Force Release a lock, Secrets Store "Make active", Rotate an API key, Bulk Apply on accounts. (W2-4, W2-6, W3-3, W4-4, W5-4, W10-1, W10-4)
3. **No "saved" message after a save that navigates away**, so the user infers success from the list. (W2-8, W3-3, W4-8, W9-8)
4. **List state isn't in the URL, and numbers don't drill down.** Dashboard, KPI and stage-summary counts are plain text, and the Accounts list reads no query string, so a filter is lost on every round trip and a report can't be shared or repeated. This is the biggest workflow gap for the daily user. (W2-1, W2-2, W7-1, W7-2)
5. **Missing empty states** (a filter with no matches shows only headers): Targets, Access Groups, Accounts, Discovered Accounts, Risk Score, Identity Providers, Field Metadata. (W2-7, W4-6, W5-2, W7-7, W10-9, W11-6)
6. **Silent failures**: bulk-import HTTP errors on Targets, Access Groups and Application mapping render nothing; a failed audit field-change load looks like "no changes"; theme, language and page-size saves ignore failures. (W4-1, W8-3, W11-3, W12-3)
7. **Accessibility basics**: a worklist that opens only on row click, "Delete" repeated with no row name, toggles without `aria-expanded`, forms that open away from the control without moving focus. (W3-5, W4-7, W5-5, W5-6, W9-9, W10-8)

## Bugs found (not polish)

| ID | What | Where |
|---|---|---|
| W5-1 | **Discovered Accounts columns are misaligned**: the body puts "Matched by" in column 5, the headers put it last, so every value from column 5 on sits under the wrong heading and the sort buttons sort other data than the column shows. | `views/reports/DiscoveredAccounts.vue:66-75,324-338` |
| W4-1 | **Targets and Access Groups bulk-import errors are never shown**: the code sets `{error}`, no template reads it, so a 400, 413 or 500 shows a summary with blank counts, and a network error shows nothing. | `TargetsBulkImport.vue:44,80-82`; `AccessGroupsBulkImport.vue:42,61-63` |
| W4-2 | **Access Group "Found On Target" lists only the first page of targets** (the API pages; about 2,380 rows exist), so most targets can't be chosen for a Local group. | `AccessGroupEdit.vue:41` |
| W3-1 | **A failed risk-exception save or extend hides the form or detail**, leaving only the error. | `RiskExceptionEdit.vue:158-161` |
| W1-3 | **After a Windows sign-in failure the other sign-in methods and Continue disappear** (the `v-else` pairs with the failure alert), so a page reload is the only retry. | `Login.vue:142-146` |
| W9-2 | **Pressing Enter in "New source system" saves the whole data feed** (the inputs sit inside the feed form). | `DataFeedEdit.vue:223-230` |
| W10-6 | **An expired API key can't be saved, even renamed**: the date input has `min=today` and `required` while the stored expiry is in the past. | `ApiKeyEdit.vue:98` |
| W8-1 | **A failed per-row Safe assignment leaves the dropdown on the value that wasn't saved**, with the error far up the page. | `ApplicationSafeMapping.vue:95-113` |
| W9-6 | Same for the Notifications target-role select. | `Notifications.vue:178-190` |
| W11-3 | A failed audit field-changes load is shown as "no field changes". | `AuditLogViewer.vue:157-159` |

## Workflow 1: Sign-in and access

Today: the router guard sends an unauthenticated user to `/login`; Login lists or auto-starts the methods; after sign-in the user lands on `returnUrl`; a user with no roles still signs in and gets the whole menu, and only My Profile says "Roles: none".

| ID | Finding | Effort |
|---|---|---|
| W1-1 | **High.** A user with no access is never told why: no page names them, their roles or who to contact; pages show "Request failed: 403" (`Dashboard.vue:26,164`, `AccountProgressList.vue:314`). Show a no-access state when `permissionNames` is empty. | M |
| W1-2 | The access-denied page is also the 404 page and offers no next step (`shell.json:131`). Split them; add links home and to My Profile. | S |
| W1-3 | Bug above (method list disappears after a Windows failure). | S |
| W1-4 | "No identity providers are enabled" flashes while loading (`Login.vue:141,147`). | S |
| W1-5 | The Identity Provider pages never say OIDC changes need an app restart (a code comment only). Unverified: wording elsewhere. | S |
| W1-6 | No empty states; Enabled prints raw true/false; row-less Delete labels (`IdentityProviders.vue:119-127`, `GroupRoleMapping.vue:137`, `RolesAndPermissions.vue:68`). | S |
| W1-7 | No lock-out warning when deleting the default or last enabled provider, or your own role. Unverified: the server may refuse. | M |

Works well: distinct idle and signed-out messages, an open-redirect-safe `returnUrl`, group names resolved with a Lookup tool that explains effective rights.

## Workflow 2: Tracking migration progress

Today: Dashboard, then the Accounts list (seven filters, multi-sort, paging), then account detail (edit lock taken at once, tabbed form, Save returns to the list), or bulk edit (select, tick fields, Apply).

| ID | Finding | Effort |
|---|---|---|
| W2-1 | **High.** The Dashboard can't drill into accounts: stage rows are plain text and the list reads no query string. | M |
| W2-2 | **High.** Filters, sort and page are lost on every round trip: filter to 200 accounts, open one, save, and you are back on an unfiltered page 1. Keep list state in the URL and return to it after Save and Cancel. | M |
| W2-3 | The edit lock can be lost silently: the heartbeat ignores its response, and the user learns at Save. Warn at once; offer a re-acquire that keeps the form. | M |
| W2-4 | Force Release takes a colleague's lock in one click, with a raw ISO time (`AccountProgressDetail.vue:530-531`). Confirm, naming the holder. | S |
| W2-5 | A view-only user isn't told why there is no edit form (`:542,651`). | S |
| W2-6 | Bulk Apply has no confirmation or summary, and doesn't use the shared bulk components; a ticked Date or text field left empty clears that field on every selected account. | M |
| W2-7 | No empty state and no Clear filters button on a seven-filter list. | S |
| W2-8 | No confirmation after Save; the risk override saves on its own, unexplained (Cancel won't undo it). | S |
| W2-9 | A reference-data load failure in bulk edit is silent (empty dropdowns). | S |

Works well: selection survives paging and sorting; sort headers are real buttons with `aria-sort`; a regression needs a reason; delete and undelete keep a visible history.

## Workflow 3: Risk exceptions

Today: the Dashboard links to the two worklists (the list page doesn't); create picks Account or Application, with a raw AccountKey for Account; the detail has Extend and Revoke; the approval worklist lists every Active exception.

| ID | Finding | Effort |
|---|---|---|
| W3-1 | Bug above. | S |
| W3-2 | **High.** Account scope needs a raw numeric AccountKey and a wrong one gets a generic 400. Use the typeahead (`Combobox.vue`, already used on `TargetEdit.vue`). | M |
| W3-3 | Revoke has no confirmation; extend and revoke give no feedback. | S |
| W3-4 | The detail shows "Account #1234", not the account's name or a link to it, and doesn't list the accounts an application-scoped exception covers. | M |
| W3-5 | The approval worklist opens only on row click: no link, no keyboard access (`RiskExceptionsApprovalWorklist.vue:286-290`). | S |
| W3-6 | The worklists can't be reached from the exceptions list; neither shows the linked account, and neither can extend or revoke inline. | S |
| W3-7 | "Approval worklist" lists every Active exception; there is no pending-approval state. Unverified whether the API has one. Rename or confirm the purpose. | S |
| W3-8 | Expired and Revoked exceptions show no reason, who or when (D-190 records it). | S |

Works well: bulk import (template, rules, summary with row errors and new IDs), the shared list pattern with bulk extend and revoke and a required reason, links to the source ticket.

## Workflow 4: Targets and access groups

Today: list with type, application and linked filters; Target Edit with identifiers and Link to another target; bulk import (inventory, account-to-target); Admin > Target Match Review (Link, New Target, Ignore); access groups with the same pattern and three import CSVs.

| ID | Finding | Effort |
|---|---|---|
| W4-1, W4-2 | Bugs above. | S, M |
| W4-3 | "Link account to target" on Targets can only pick a target on the current page, with a free-text account name and a status-code-only error (`Targets.vue:359-364`). Use the search Combobox. | M |
| W4-4 | Target Match Review has no confirm on Ignore (remembered permanently), no message saying what was resolved, and buttons with no row context (`TargetMatchReview.vue:72-130`). | S |
| W4-5 | No path from an import's "N pending review" to the queue, which is only under Admin. | S |
| W4-6 | No empty states on Targets and Access Groups; a failed load keeps the stale table. | S |
| W4-7 | Delete buttons have no row label (`Targets.vue:347`, `AccessGroups.vue:283`). | S |
| W4-8 | No "saved" message on return to the list, and list state isn't kept. | S |

Works well: link and unlink confirmations that say what will happen, the delete confirmation that names the target, a readable 409 for a duplicate access group, column checks before an import.

## Workflow 5: Reconciliation and discovery

| ID | Finding | Effort |
|---|---|---|
| W5-1 | Bug above (Discovered Accounts columns). | S |
| W5-2 | Discovered Accounts has no empty state. | S |
| W5-3 | Reconciliation Review is a dead end: no hint, read-only ("actions aren't wired up yet" in its own comment), and nothing links it to Possible Matches, which does the linking. Add a hint and link, or retire it if superseded (unverified). | S |
| W5-4 | Dismiss on Possible Matches and on Discovered Accounts has no confirm, undo or "show dismissed" view, while Link confirms. | M |
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
| W7-1 | **High.** No drill-down from a number to the accounts behind it (KPI, stage summary, Dashboard). | M |
| W7-2 | Report filters aren't in the URL, so a report can't be shared or repeated (Trends, Discovered Accounts, Risk Score). | M |
| W7-3 | KPI Summary doesn't say what Onboarded, Managed or Compliant mean, or give an "as of" date (the definitions are in a code comment, D-143). | S |
| W7-4 | Overdue/At-Risk can't be sorted, filtered or paged, and shows no days overdue. | M |
| W7-5 | Trends doesn't validate the date range and refetches on every keystroke. | S |
| W7-6 | The Risk Score account name expands a panel instead of linking to the account. | S |
| W7-7 | Risk Score has no empty state. | S |

Works well: exports respect the active filters and sort and relay the API's error; Trends has chart text alternatives and a data table behind each chart.

## Workflow 8: Application mapping

| ID | Finding | Effort |
|---|---|---|
| W8-1 | Bug above (per-row Safe assignment). | S |
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
| W9-2 | Bug above (Enter in "New source system"). | S |
| W9-3 | The SMTP Test email uses the saved settings but stays enabled while the form is dirty. | S |
| W9-4 | Failed runs, Unhealthy checks and failed backups aren't marked in the run history, health checks or backup table. | S |
| W9-5 | Add-recipient failure shows only the status code. | S |
| W9-6 | Bug above (target-role select). | S |
| W9-7 | Backup App has no confirmation and no success line naming the file. | S |
| W9-8 | No "Saved" message after a feed or recipient save. | S |
| W9-9 | The run-history time is an `<a href="#">` toggle with no `aria-expanded`. | S |

Works well: the business-hours guard on Run now, Test on unsaved feed values, Global Application Configuration's save feedback and pattern tester.

## Workflow 10: Security setup

| ID | Finding | Effort |
|---|---|---|
| W10-1 | **High.** Secrets Store "Make active" switches where every privileged-account secret is fetched from with no confirmation and no success message (`SecretsStoreConfiguration.vue:79-101`). | S |
| W10-2 | Identity Provider pages never say OIDC changes need an app restart. | S |
| W10-3 | No lock-out warning when deleting an identity provider. | S |
| W10-4 | Rotate an API key has no confirmation and no leave-page guard on the reveal screen. | S |
| W10-5 | Copy failure is silent on plain-HTTP hosts (`ApiKeyReveal.vue`). | S |
| W10-6 | Bug above (expired API key). | S |
| W10-7 | SIEM Save and Test messages appear at the top of a long form, away from the buttons. | S |
| W10-8 | Secrets Store credential inputs have no label; the Role permission list isn't a fieldset. | S |
| W10-9 | Identity Provider and Role save errors show only the status; no empty states; raw true/false. | S |

Works well: the API key reveal (shown once, select on focus, Copy), the keys list (status, last used, where from), SIEM status and "Test first" hint.

## Workflow 11: Audit and fields

| ID | Finding | Effort |
|---|---|---|
| W11-1 | Event type is free text and there is no filter for who did it. Unverified: whether the API supports a user filter. | M |
| W11-2 | Field Metadata type and reference table are free text; a typo can break the Account Progress edit form. Use selects. | M |
| W11-3 | Bug above (failed load looks like "no changes"). | S |
| W11-4 | Audit timestamps are the raw stored string with no zone. | S |
| W11-5 | No Clear filters button. | S |
| W11-6 | Field list prints true/false; no empty state. | S |

## Workflow 12: Personal settings

| ID | Finding | Effort |
|---|---|---|
| W12-1 | Idle sign-out gives no warning and no "Stay signed in", and a user mid-form loses the edits (`useIdleSignOut.js:40-52`). Unverified: whether sign-in returns to the page. | M |
| W12-2 | Reload My Rights replaces the whole page with "Loading" and gives no confirmation; a failure replaces the page with an alert. | S |
| W12-3 | Theme, language and page-size saves ignore failures and show no "Saved". | S |
| W12-4 | Page size can be set only from list pagers, not on the profile page. | S |
| W12-5 | A sign-out error appears detached, under a menu that has already closed. Unverified how it looks. | S |

Works well: the user menu is a proper disclosure (Escape returns focus, closes on route change), the About dialog is modal with focus handled, the idle timer is shared across tabs.

## Suggested order

1. **Quick, high-value fixes (all S):** the bug table above, and the missing confirmations (W10-1, W3-3, W4-4, W2-4).
2. **List state in the URL and drill-down** (W2-1, W2-2, W7-1, W7-2): the biggest change to the daily workflow, and one shared piece of work.
3. **One shared error helper** for failed saves (theme 1), then empty states (theme 5) and "saved" messages (theme 3).
4. **No-access experience** (W1-1, W1-2).
5. The rest by workflow.
