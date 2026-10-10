/* ============================================================================
   01_BlueTrack_CreateDatabase.sql

   D-195: this folder (Database/Manual) holds the scripts App/Migrator never
   runs -- it reads only the top level of Database/. Each is run by hand
   (sqlcmd or SSMS) or by Deploy/Install-BlueTrack.ps1.

   Optional: creates the database by hand, before App/Migrator is pointed at
   a brand-new environment. It can't be a Migrator script: DbUp opens
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
