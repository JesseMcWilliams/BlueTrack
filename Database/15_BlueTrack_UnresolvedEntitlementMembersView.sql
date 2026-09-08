/* ============================================================================
   15_BlueTrack_UnresolvedEntitlementMembersView.sql

   RUN THIS AFTER 01-14. Guarded (CREATE OR ALTER VIEW is always safe to
   re-run -- a view holds no state of its own) -- per D-58, this is a small
   incremental script appended after the 2026-09-05 restructure (D-106)
   rather than an edit to an existing numbered file, since the database now
   holds real loaded data.

   D-108-adjacent finding (Design_Decision_Register.md, D-107): 74 of 111
   entitlement rows on the first real Privilege Cloud load were granted
   directly to a CyberArk Identity/Entra-federated user or a built-in cloud
   role -- referenced by a GUID or role name that never appears in
   stg_pc_users/stg_pc_groups, so dim_user/dim_group can never resolve
   them. D-107 added dbo.fact_safe_entitlement.UnresolvedMemberId to
   preserve these rows instead of dropping them. This view surfaces them
   for the UI (Reports > Unresolved Entitlement Members, requested by the
   user 2026-09-06) -- neither vw_effective_safe_access nor
   vw_powerbi_entitlements (Database/04, Database/06) can show these rows,
   since both inner-join to dim_user/dim_group by design.

   These are typically internal/service identities (a built-in cloud role,
   or a federated principal CyberArk Identity Administration manages
   outside this app's own Privilege Cloud exports) -- not a data-quality
   defect to chase down, but worth surfacing so an analyst reviewing safe
   access knows a safe has a member this app can't put a real name to.
   ============================================================================ */

USE $DatabaseName$;
GO

CREATE OR ALTER VIEW vw_unresolved_entitlement_members AS
SELECT
    fse.EntitlementKey,
    fse.SafeKey,
    ds.SafeName,
    fse.MemberType,
    fse.UnresolvedMemberId,
    fse.MembershipExpirationDate,
    fse.SnapshotDate
FROM dbo.fact_safe_entitlement fse
JOIN dbo.dim_safe ds ON ds.SafeKey = fse.SafeKey
WHERE fse.UnresolvedMemberId IS NOT NULL;
GO

PRINT 'vw_unresolved_entitlement_members created.';
