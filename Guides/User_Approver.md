# User Guide: Approver

**Blueprint Progress Tracking Web Interface**

## Who this is for

`ViewDashboard` + `EditAccountProgress` + `ApproveExceptions` + `ConfirmReconciliation` — reviewing and approving Risk Exceptions, and linking them to accounts.

## The Dashboard

**Where**: `Dashboard` in the top nav. The **Risk Exceptions Needing Attention** card also shows a count of Active exceptions awaiting your approval.

## Risk Exceptions

**Where**: **Exceptions** in the top nav (`/exceptions`).

- **The list** — filter by Status (Active/Expired/Revoked) and Scope (Account/Application). Use **+ New Exception** to create one.
- **Creating one** — choose Account or Application scope, fill in Justification, Review Date, and an optional External Ticket Reference, then **Create Exception**.
- **Managing an existing one** — while Active, **Re-approve** extends the Review Date; **Revoke** ends it immediately.
- **Approval Worklist** (`Exceptions > Approval Worklist`) — every currently-Active exception.
- **Overdue Reviews** (`Exceptions > Overdue Reviews`) — Active exceptions past their Review Date.

## Linking an exception to an account

Open an account under **Accounts**, and on its **Risk Exception** tab (visible when Status is "Risk Accepted / Excluded"), pick an existing Active exception or create one inline.

If your organization has segregation of duties turned on (`Admin > Global Application Configuration`), you can't link an exception you approved yourself — pick a different exception, or have a colleague link it.

## Reconciliation Review

**Where**: **Reports > Reconciliation Review**.

Unconfirmed cross-source account matches — currently read-only.

## See also

- `User_Guide.md` — the full reference guide for every page.
