/* ============================================================================
   17_BlueTrack_AccountTypeSeed.sql

   RUN THIS AFTER 01-16.

   dim_account_type (01_BlueTrack_CreateDatabase_Schema.sql) was created
   with its intended values only as a code comment, never an actual INSERT
   -- found empty while building the Account Progress edit form's
   AccountType dropdown (dim_account_type is one of its Dropdown fields).
   Seeded with exactly the values already named in that comment.

   Guarded -- safe to re-run.
   ============================================================================ */

USE $DatabaseName$;
GO

INSERT INTO dbo.dim_account_type (AccountTypeName)
SELECT v.AccountTypeName
FROM (VALUES
    ('Domain Account'), ('Local/OS Account'), ('Cloud IAM'), ('Database Account'),
    ('Network Device'), ('Application/Service Account'), ('DevOps Secret'),
    ('RPA Account'), ('Infrastructure (PSM/CPM)'), ('Emergency/Break-glass')
) AS v(AccountTypeName)
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.dim_account_type t WHERE t.AccountTypeName = v.AccountTypeName
);

PRINT 'dim_account_type seeded.';
