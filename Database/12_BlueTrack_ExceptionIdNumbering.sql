/* ============================================================================
   12_BlueTrack_ExceptionIdNumbering.sql

   RUN THIS AFTER 01-11.

   D-17 (Design_Risk_Exception_Tracking.md): risk_exception.ExceptionID's
   numbering scheme must be admin-configurable, not a hardcoded format --
   it differs by organization. Adds that configuration to web.app_config
   (a singleton table -- exactly one row, per its existing seed in
   06_BlueTrack_WebInterface_Schema.sql):

     - ExceptionIdPattern: the format string. Supported tokens (parsed by
       App/Api/Data/ExceptionIdGenerator.cs): {yyyy} (4-digit year),
       {yy} (2-digit year), {seq:0000} (the running sequence, zero-padded
       to however many 0s appear -- e.g. {seq:0000} -> "0007").
       Default 'EXC-{yyyy}-{seq:0000}' matches the design doc's own example,
       EXC-2026-0001.
     - ExceptionIdSequenceYear / ExceptionIdNextSequence: the running
       counter, reset to 1 whenever the current year no longer matches
       ExceptionIdSequenceYear -- so numbering restarts each year by
       default (a reasonable default given the pattern's own {yyyy} token;
       an org that wants a non-resetting global sequence can still get one
       by using a pattern without {yyyy}, since the reset only affects the
       *number*, not whether it's shown).

   Column adds are guarded independently (COL_LENGTH), matching the pattern
   used elsewhere in this project for altering an existing table without
   dropping it (see 06's dim_safe.ApplicationKey for the precedent).
   ============================================================================ */

USE $DatabaseName$;
GO

IF COL_LENGTH('web.app_config', 'ExceptionIdPattern') IS NULL
BEGIN
    ALTER TABLE web.app_config ADD ExceptionIdPattern NVARCHAR(50) NOT NULL DEFAULT 'EXC-{yyyy}-{seq:0000}';
END
GO

IF COL_LENGTH('web.app_config', 'ExceptionIdSequenceYear') IS NULL
BEGIN
    ALTER TABLE web.app_config ADD ExceptionIdSequenceYear INT NULL;
END
GO

IF COL_LENGTH('web.app_config', 'ExceptionIdNextSequence') IS NULL
BEGIN
    ALTER TABLE web.app_config ADD ExceptionIdNextSequence INT NOT NULL DEFAULT 1;
END
GO

PRINT 'web.app_config: ExceptionID numbering columns added (D-17).';
