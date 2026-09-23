# User Guide: Approver

**Blueprint Progress Tracking Web Interface**

## Who this is for

Someone whose BlueTrack role grants `ViewDashboard`, `EditAccountProgress`, `ApproveExceptions`, and `ConfirmReconciliation` — reviewing and approving Risk Exceptions, and linking them to accounts. This role is a sibling of the Analyst role ([User_Analyst.md](User_Analyst.md)), not a subset or superset of it — an Approver can approve exceptions and confirm reconciliation but cannot manage Targets or Access Groups, while an Analyst can manage those but can't approve exceptions.

## The Dashboard

**Where**: the home page after signing in (`Dashboard` in the top nav). The **Risk Exceptions Needing Attention** card is the one most relevant to this role — since you hold `ApproveExceptions`, it also shows a count of Active exceptions specifically awaiting your approval.

## Risk Exceptions

**Where**: **Exceptions** in the top nav (`/exceptions`).

This is the core of the Approver role:
- **The list** — filter by Status (Active/Expired/Revoked) and Scope (Account/Application). Because you hold `ApproveExceptions`, a **+ New Exception** button appears.
- **Creating one** — choose Account or Application scope, fill in Justification, Review Date, and an optional External Ticket Reference, then **Create Exception**.
- **Managing an existing one** — while Active, **Re-approve** extends the Review Date without changing anything else; **Revoke** ends it immediately.
- **Approval Worklist** (`Exceptions > Approval Worklist`) — every currently-Active exception, for a working queue view.
- **Overdue Reviews** (`Exceptions > Overdue Reviews`) — Active exceptions past their Review Date, needing your attention.

## Linking an exception to an account

Because you also hold `EditAccountProgress`, you can open an account under **Accounts** and, on its **Risk Exception** tab (visible when Status is set to "Risk Accepted / Excluded"), pick an existing Active exception or create one inline.

**A note on segregation of duties**: if your organization has turned on the segregation-of-duties setting (`Admin > Global Application Configuration`), you personally cannot link an exception here if you're the one who approved it — you'll get a validation error explaining why. This doesn't restrict who can approve exceptions in general, only the specific approver-and-linker pairing for one exception. If you hit this, pick a different exception, or have a colleague link it.

## Reconciliation Review

**Where**: **Reports > Reconciliation Review** (requires `ConfirmReconciliation`).

Unconfirmed cross-source account matches — currently a read-only view; there's no confirm/reject action wired up yet.

## See also

- `User_Guide.md` — the full reference guide for every page.
- `Design Documents/Design_Risk_Exception_Tracking.md` — the full Risk Exception approval workflow, including the segregation-of-duties check.
