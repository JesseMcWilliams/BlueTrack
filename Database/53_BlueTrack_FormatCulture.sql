/* ============================================================================
   53_BlueTrack_FormatCulture.sql

   RUN THIS AFTER 01-52. Guarded -- safe to re-run.

   D-294 (the user's answers of 2026-10-21): English only, but numbers and
   dates follow other cultures. The web pages already use each viewer's
   browser or chosen language; text the server writes for people (emails,
   feed run and Self-Hosted pull summaries) uses one installation-wide
   culture, set on Global Application Configuration.

     web.app_config.FormatCulture   a .NET culture name: en-US (default),
                                    en-GB, de-DE, fr-FR ...
   ============================================================================ */

USE $DatabaseName$;
GO

IF COL_LENGTH('web.app_config', 'FormatCulture') IS NULL
    ALTER TABLE web.app_config ADD FormatCulture NVARCHAR(20) NOT NULL
        CONSTRAINT DF_app_config_FormatCulture DEFAULT (N'en-US');
GO
