/* ============================================================================
   23_BlueTrack_UserPreferenceSchema.sql

   RUN THIS AFTER 01-22.

   web.user_preference (Design_Accessibility_And_Theming.md, D-93): a
   generalized per-user preferences store, keyed by an arbitrary
   PreferenceKey rather than one column per setting -- the user's explicit
   choice, 2026-09-04, so a future preference beyond the first one (Theme)
   doesn't need its own schema migration. Composite PK (UserKey,
   PreferenceKey): exactly one value per preference per user, upserted by
   the API rather than enforced by a database trigger.

   Guarded -- safe to re-run.
   ============================================================================ */

USE $DatabaseName$;
GO

IF OBJECT_ID('web.user_preference', 'U') IS NULL
BEGIN
    CREATE TABLE web.user_preference (
        UserKey           INT              NOT NULL REFERENCES web.app_user(UserKey),
        PreferenceKey      NVARCHAR(50)     NOT NULL,
        PreferenceValue     NVARCHAR(200)    NOT NULL,
        ModifiedDate          DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_user_preference PRIMARY KEY (UserKey, PreferenceKey)
    );
END
GO

PRINT 'web.user_preference created (D-93).';
