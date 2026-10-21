# User Guide: Approver

**Blueprint Progress Tracking Web Interface**

## Who this is for

`ViewDashboard` + `EditAccountProgress` + `ApproveExceptions` + `ConfirmReconciliation` — reviewing and approving Risk Exceptions, and linking them to accounts.

## The Dashboard

**Where**: `Dashboard` in the top nav. The **Risk Exceptions Needing Attention** card also shows a count of Active exceptions awaiting your approval.

## Risk Exceptions

**Where**: **Exceptions** in the top nav (`/exceptions`).

- **The list** — filter by Status (Active/Expired/Revoked) and Scope (Account/Application). Use **+ New Exception** to create one. The **Source** column shows where an imported exception came from.
- **Creating one** — choose Account or Application scope (for an account, type part of its user name, account name or address and choose it from the list), fill in Justification, Review Date, and an optional External Ticket Reference, then **Create Exception**.
- **Managing an existing one** — the page names the account (a link) or the application, and for an application lists the accounts it covers. While Active, **Extend Review Date** re-approves it with a new Review Date; **Revoke Exception** needs a **Reason to revoke** and ends it immediately once you confirm. An exception that's no longer Active can't be re-approved or revoked.
- **Bulk actions** — tick **Select exceptions for bulk actions**, select exceptions, then **Extend review…** (a new review date for all of them) or **Revoke…** (needs a reason). Exceptions that aren't Active are skipped and listed.
- **Importing from another tool** — **Bulk Actions** opens a CSV import for exceptions approved elsewhere: **Download template**, fill it, then **Import**. Each row gets a new BlueTrack Exception ID and keeps its source tool, ID and link; it can also link an account exception to its account. See `User_Guide.md`, "Importing exceptions approved in another tool".
- **Approval Worklist** (`Exceptions > Approval Worklist`) — every currently-Active exception.
- **Overdue Reviews** (`Exceptions > Overdue Reviews`) — Active exceptions past their Review Date.

## Linking an exception to an account

Open an account under **Accounts**, and on its **Risk Exception** tab (visible when Status is "Risk Accepted / Excluded"), pick an existing Active exception or create one inline.

If your organization has segregation of duties turned on (`Admin > Global Application Configuration`), you can't link an exception you approved yourself — pick a different exception, or have a colleague link it.

## Reconciliation Review

**Where**: **Reports > Reconciliation Review**.

Unconfirmed matches between a Self-Hosted and a Privilege Cloud account, for review only: the page has no confirm or reject buttons.

## Possible Account Matches

**Where**: **Reports > Possible Account Matches**.

Accounts from other systems (account inventory data feeds) that may be the same as a CyberArk account, but didn't match it exactly: the user name is the same, and the addresses agree by host name (`web01` and `web01.corp.example.com`) or by DNS. Each row shows both accounts and why they were paired.

- **Link** treats them as one account, as an exact match would: the other system's account is hidden from Accounts and the reports, and moves from **Discovered** to **Onboarded to Vault**. You're asked to confirm first.
- **Dismiss** means they're different accounts; the pair isn't suggested again. You're asked to confirm first.
- **Show dismissed matches** lists the dismissed pairs, with **Restore** to bring one back.

Both are recorded in the Audit Log.

## Unresolved Entitlement Members

**Where**: **Reports > Unresolved Entitlement Members**.

Safe members BlueTrack couldn't match to a user or group from the Users and Groups exports, with their name and ID. Members whose exact name belongs to one user or group are matched by the nightly load and aren't listed. The **Source** column says whether a member is from Privilege Cloud or Self-Hosted.

- **Resolve** — link the member to the right user or group of its own source (Privilege Cloud or Self-Hosted) (search by name; the one with exactly the member's name is filled in), or choose **External / cloud role** with an optional note. It applies to the member on every safe and is kept through the nightly load.
- **Show resolved** — also lists resolved members and how each was resolved; **Undo** removes a resolution you or a colleague made.

Both are recorded in the Audit Log.

## Exporting reports

Every report you can view, including these, has **Export CSV** and **Export Excel**: the file holds all rows matching the report's current filters and sort, up to the administrator's **Export Max Rows** limit (a larger export is refused with the row count). Each export is recorded in the Audit Log. **Reports > Trends** shows stages, % complete and risk levels over time. See `User_Guide.md`, Reports.

## See also

- `User_Guide.md` — the full reference guide for every page.
