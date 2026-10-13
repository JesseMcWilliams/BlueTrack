# User Guide: Approver

**Blueprint Progress Tracking Web Interface**

## Who this is for

`ViewDashboard` + `EditAccountProgress` + `ApproveExceptions` + `ConfirmReconciliation` — reviewing and approving Risk Exceptions, and linking them to accounts.

## The Dashboard

**Where**: `Dashboard` in the top nav. The **Risk Exceptions Needing Attention** card also shows a count of Active exceptions awaiting your approval.

## Risk Exceptions

**Where**: **Exceptions** in the top nav (`/exceptions`).

- **The list** — filter by Status (Active/Expired/Revoked) and Scope (Account/Application). Use **+ New Exception** to create one. The **Source** column shows where an imported exception came from.
- **Creating one** — choose Account or Application scope, fill in Justification, Review Date, and an optional External Ticket Reference, then **Create Exception**.
- **Managing an existing one** — while Active, **Extend Review Date** re-approves it with a new Review Date; **Revoke Exception** ends it immediately and needs a **Reason to revoke**. An exception that's no longer Active can't be re-approved or revoked.
- **Bulk actions** — tick **Select exceptions for bulk actions**, select exceptions, then **Extend review…** (a new review date for all of them) or **Revoke…** (needs a reason). Exceptions that aren't Active are skipped and listed.
- **Importing from another tool** — **Bulk Actions** opens a CSV import for exceptions approved elsewhere: **Download template**, fill it, then **Import**. Each row gets a new BlueTrack Exception ID and keeps its source tool, ID and link; it can also link an account exception to its account. See `User_Guide.md`, "Importing exceptions approved in another tool".
- **Approval Worklist** (`Exceptions > Approval Worklist`) — every currently-Active exception.
- **Overdue Reviews** (`Exceptions > Overdue Reviews`) — Active exceptions past their Review Date.

## Linking an exception to an account

Open an account under **Accounts**, and on its **Risk Exception** tab (visible when Status is "Risk Accepted / Excluded"), pick an existing Active exception or create one inline.

If your organization has segregation of duties turned on (`Admin > Global Application Configuration`), you can't link an exception you approved yourself — pick a different exception, or have a colleague link it.

## Reconciliation Review

**Where**: **Reports > Reconciliation Review**.

Unconfirmed cross-source account matches — currently read-only.

## Possible Account Matches

**Where**: **Reports > Possible Account Matches**.

Accounts from other systems (account inventory data feeds) that may be the same as a CyberArk account, but didn't match it exactly: the user name is the same, and the addresses agree by host name (`web01` and `web01.corp.example.com`) or by DNS. Each row shows both accounts and why they were paired.

- **Link** treats them as one account, as an exact match would: the other system's account is hidden from Accounts and the reports, and moves to **Onboarded to Vault**. You're asked to confirm first.
- **Dismiss** means they're different accounts; the pair isn't suggested again.

Both are recorded in the Audit Log.

## See also

- `User_Guide.md` — the full reference guide for every page.
