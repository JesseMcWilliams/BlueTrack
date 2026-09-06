/* ============================================================================
   00_BlueTrack_CreateDatabase.sql

   RUN THIS ONCE, BY HAND, BEFORE EVER POINTING App/Migrator AT A BRAND-NEW
   ENVIRONMENT. It is deliberately NOT part of the numbered DbUp-managed
   sequence (01 onward) -- App/Migrator always excludes any file matching
   `00_*.sql` from what it hands to DbUp, regardless of any skip-list
   argument, as a defensive backstop (see App/Migrator/Program.cs's own
   comment). This is a structural requirement, not a convenience: DbUp opens
   one connection scoped to a single target database for its entire run and
   writes its own journal (dbo.SchemaVersions) inside that same database --
   a script that switches context to `master` to create a database would
   leave DbUp trying to write its journal into the wrong database (or a
   database that doesn't have that table at all), which is exactly the kind
   of hardcoded-assumption bug D-89 already found and fixed once. Database
   creation has to happen in a completely separate connection/step, before
   DbUp ever opens its own.

   WHAT THIS ACTUALLY DOES: create the target database if it doesn't already
   exist. Never drops one -- confirmed intentional, matching
   App/Migrator/Program.cs's own bootstrap logic, which does the exact same
   check-then-create against `master` automatically before every Migrator
   invocation. This script exists so that behavior is also visible and
   runnable independently of the Migrator tool -- for a DBA standing up a
   brand-new server who wants to see exactly what gets created before any
   application tooling touches it, or for any environment where running
   App/Migrator isn't the intended first step.

   Replace $DatabaseName$ below with your actual target database name
   (e.g. BlueTrack, BlueTrackTest) before running this by hand -- it is
   DbUp's own substitution token elsewhere in this project, but since this
   file is never passed through DbUp, nothing will substitute it for you.

   A caller that wants a genuinely fresh/rebuilt database (this project's
   own disposable BlueTrackTest, or the real BlueTrack database during this
   project's still-in-development phase) drops it explicitly first, as its
   own separate, visible step (see .github/workflows/ci.yml's "Drop
   BlueTrackTest if it exists" step for the pattern) -- never something this
   script or App/Migrator does silently.
   ============================================================================ */

USE master;
GO

IF DB_ID(N'$DatabaseName$') IS NULL
BEGIN
    CREATE DATABASE [$DatabaseName$];
    PRINT 'Database $DatabaseName$ created.';
END
ELSE
BEGIN
    PRINT 'Database $DatabaseName$ already exists -- nothing to do.';
END
GO
