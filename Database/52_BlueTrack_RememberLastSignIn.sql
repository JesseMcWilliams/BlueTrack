/* ============================================================================
   52_BlueTrack_RememberLastSignIn.sql

   RUN THIS AFTER 01-51. Guarded -- safe to re-run.

   D-292 (the user's answer of 2026-10-21, extending D-205): the sign-in page
   can start the method a browser last signed in with, instead of the
   default sign-in method.

     web.app_config.RememberLastSignInMethod   0 (default) = always the default
                                               sign-in method, as before;
                                               1 = the method this browser last
                                               signed in with, if it's still
                                               enabled, else the default.
   ============================================================================ */

USE $DatabaseName$;
GO

IF COL_LENGTH('web.app_config', 'RememberLastSignInMethod') IS NULL
    ALTER TABLE web.app_config ADD RememberLastSignInMethod BIT NOT NULL
        CONSTRAINT DF_app_config_RememberLastSignInMethod DEFAULT (0);
GO
