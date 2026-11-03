# BlueTrack: Safe Access Policies and Compliance Reviews

> **Stage: Design.** How the Safe access check (D-300) and the per-account compliance review (D-301) work. Requested 2026-11-03: "a checklist of criteria for an account/Safe to enter the Managed state", applied at the Safe level, then one manual review per account.

## What it answers

At 7,000+ accounts, whether an account can be called Managed or Compliant depends on its Safe as much as on the account: the right groups are members with the right permissions, the groups have people in them, and members that shouldn't be there (Vault Admins) aren't. A **Safe access policy** states that per group of Safes; the nightly load checks every Safe with accounts against it; **Progress Rules** (D-283) then use the result, with the account's own fields (owner, application, CPM status) and its **compliance review**.

## Model (script 57)

| Object | Meaning |
|---|---|
| `web.safe_role`, `web.safe_role_permission` | A named set of Safe permissions (`dbo.dim_permission` names). A member holds the role when every one is granted to it (`bridge_entitlement_permission.IsGranted`); with `ExactMatch`, also no other permission. |
| `web.safe_access_policy` | `SafeNamePattern` (`*` is any text, case-insensitive), `SortOrder`, `IsEnabled`. A Safe is checked against the **first enabled policy, in SortOrder**, whose pattern matches its name; so narrower policies go first. |
| `web.safe_access_policy_rule` | `Required`: a member whose name matches `MemberPattern` holds `SafeRoleKey`, and with `RequirePeople`, has people in it (a user is one; a group needs a row in `dbo.vw_safe_access_people`: Vault group members or AD users, D-281). `Forbidden`: no member matching the pattern (holding the role, when one is given). `{SafeName}` in a member pattern is the Safe's name. |
| `dbo.safe_access_result`, `dbo.safe_access_problem` | The last check: per Safe with an account not deleted, the policy that applied (NULL: none), `IsCompliant` (NULL with no policy) and one problem line per failed rule or forbidden member. Replaced on every check. |
| `web.account_compliance_review` | One row per reviewed account: who, when, an optional note, and the Safe it was in. |

Patterns become `LIKE` patterns through `dbo.fn_SafeAccessLike`: `[ % _` are escaped in the pattern and in the Safe's name, then `*` becomes `%` and `{SafeName}` the escaped name. A member's name is `dim_user.Username` or `dim_group.GroupName`.

## The check

`dbo.usp_Check_SafeAccessPolicies` runs in `usp_RunFullLoad` **after `usp_Load_FactSafeEntitlement`, which moved before the Progress Rules** (it reads only staging and the dims), so tonight's rules see tonight's result; and on **Check now** (Admin page). It first deletes the review of any account whose Safe changed since it was reviewed (D-301), then rebuilds the result and problem tables. Problem texts: "No member matches X (needs R).", "X: doesn't hold R.", "X: has no people in it (Vault or AD).", "X: must not be a member.", "X: must not hold R."

Caveats: `ExactMatch` compares the permission rows as loaded, so a source difference counts (Self-Hosted Direct loads `MoveAccountsAndFolders` as MoveAccountsFrom and MoveAccountsInto; `Design_CyberArk-Direct-Integration.md`); built-in members such as Vault Admins are loaded for Self-Hosted Direct (`IsPredefinedUser`), so a Forbidden rule can find them.

## Reviews (D-301)

`ReviewAccountCompliance` (Admin, Approver) marks or clears an account's review (account page, **Compliance** section) or reviews every not-yet-reviewed account of a Safe at once (Reports > Safe Access, **Review accounts**; still one review row per account). A review stays until the account moves to another Safe or someone clears it; a Safe failing later doesn't clear it (the user's choice). Audit-logged as AccountComplianceReviewed, one event per account.

## Progress Rule fields

`SafeAccessCompliant` (Yes/No; empty when no policy applies) and `ComplianceReviewed` (Yes/No), in `dbo.fn_ProgressAccountFields`. Example: Onboarded → Managed when CPM status is success, Owner is not empty, Application is not empty and Safe access compliant is Yes; Managed → Compliant when Compliance reviewed is Yes.

## Pages and API

| Page | API | Permission |
|---|---|---|
| Admin > Safe Access Policies (policies, Safe roles, Check now) | `GET/POST/PUT/DELETE /api/admin/safe-access/roles|policies`, `GET .../permissions`, `POST .../check` | `ManageSafeAccessPolicies` (Admin); audit SafeAccessPolicyChanged |
| Reports > Safe Access | `GET /api/reports/safe-access`; `POST /api/reports/safe-access/{safeKey}/review` | ViewDashboard; ReviewAccountCompliance to review |
| Account page, Compliance | `GET /api/account-progress/{key}/compliance`; `PUT/DELETE .../compliance-review` | ViewDashboard; ReviewAccountCompliance to change |

Seeded once, to edit: a Safe role "End user" (ListAccounts, UseAccounts, RetrieveAccounts) and a **disabled** policy "Example: every Safe" (Required `{SafeName}_Users` holding End user with people; Forbidden `Vault Admins`). The names are a suggestion, not a CyberArk default.

## Not built

- No export on Reports > Safe Access, and no notification for newly failing Safes.
- Groups are matched by their Vault name only; nothing looks an AD group up by a naming formula unless it is a Safe member (D-281 reads the members of Safe-member groups).
