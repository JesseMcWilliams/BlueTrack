using DbUp;
using Microsoft.Data.SqlClient;

// BlueTrack.Migrator: runs Database/*.sql against BlueTrack via DbUp (D-58, D-67).
//
// Usage:
//   BlueTrack.Migrator <connectionString> <scriptsFolderPath> [skipScriptNames]
//
// <connectionString> and <scriptsFolderPath> are required -- this is a
// small deploy-time utility, not a long-running service, so there's no
// appsettings.json here (consistent with keeping dependencies minimal).
//
// [skipScriptNames] is optional: a comma-separated list of script
// filenames (matched exactly) to exclude from this run, for whatever
// environment-specific reason a caller needs.
//
// Regardless of [skipScriptNames], this tool ALWAYS excludes any script
// whose filename matches `00_*.sql` -- see Database/00_BlueTrack_CreateDatabase.sql's
// own header for why: DbUp opens one connection scoped to a single target
// database for its entire run and writes its own journal inside that same
// database, so a script that switches context to `master` to create a
// database structurally cannot be part of the normal DbUp-managed sequence
// (2026-09-05 restructure). Database creation already happens separately,
// just below, via this tool's own master-connection bootstrap -- 00's
// script exists for visibility/manual use, not because this tool needs it.
//
// This tool ALSO always excludes 14_BlueTrack_ScheduleImportLoadJob.sql,
// for every environment -- not only disposable ones. Confirmed by an
// actual failed run against the real BlueTrack database (2026-09-05,
// during the same restructure that added 00's exclusion): 14's own
// `USE msdb;` succeeds and creates the job correctly, but DbUp's journal
// write for that same script then executes against whatever database the
// connection is CURRENTLY on -- which is now msdb, not the target database
// named by <connectionString> -- since 14 never switches back. That
// journal write fails with "Invalid object name 'SchemaVersions'"
// (msdb has no such table), which DbUp treats as the whole run failing,
// even though 14's actual schema-job-creation work already succeeded.
// This isn't fixable by ordering 14 last: DbUp still journals immediately
// after each script runs, on the same connection, regardless of position.
// 14 is therefore run manually (e.g. via sqlcmd, directly against msdb),
// never through this tool -- matching how 00 is handled, for a different
// but similarly structural reason.
//
// The target database name is parsed out of <connectionString>'s own
// Database/Initial Catalog and is the ONLY source of truth for which
// database the scripts touch -- it's passed into every script as DbUp's
// $DatabaseName$ substitution variable (see every numbered script's
// `USE $DatabaseName$;`). Previously the scripts hardcoded the literal
// database name "BlueTrack", independent of whatever database
// <connectionString> actually pointed at -- a real incident (2026-09-03)
// ran this tool with a connection string pointed at BlueTrackTest and it
// silently dropped and recreated the real BlueTrack database instead,
// because 01's DROP/CREATE DATABASE and every script's USE statement
// referenced "BlueTrack" by hardcoded name, not whatever <connectionString>
// said. That class of mismatch is now structurally impossible: there is
// exactly one place the database name comes from.
//
// Before DbUp runs anything, this tool connects to `master` on the same
// server/credentials and ensures the target database exists (CREATE
// DATABASE if missing -- never DROP). That's the only thing that ever runs
// against `master`; DbUp itself then connects directly to the target
// database for its entire journal-tracked run, so dbo.SchemaVersions lives
// correctly inside whichever database was actually built, not shared
// across every database this tool has ever touched. This tool never drops
// a database -- 01_BlueTrack_CoreSchema.sql no longer does
// either (its old DROP/CREATE DATABASE preamble was removed for exactly
// this reason: SQL Server can't drop a database a connection is currently
// using, which is what the 2026-09-03 incident's fix required). A caller
// that wants a genuinely fresh database (CI's disposable BlueTrackTest,
// per Design_Testing_Strategy.md) drops it explicitly, as its own visible
// step, before invoking this tool -- not something this shared tool does
// silently on every run, since most callers (real Dev/Staging/Prod
// environments) must never have their database dropped.
//
// IMPORTANT -- 01_BlueTrack_CoreSchema.sql (and every other 0x-numbered
// schema/seed script through 08) unconditionally drops and recreates every
// table it defines, or is guarded but still assumes a fresh install (see
// each script's own header and D-58) -- running this tool against an
// environment that already holds real tracked data is destructive to that
// data. This project's own environments are rebuilt from scratch (drop +
// recreate the database, then run this tool from 00 forward) rather than
// incrementally migrated, for as long as the project stays in this initial
// development phase; once a real environment holds data worth preserving,
// further schema changes must go back to being small guarded numbered
// scripts appended after 14, never edits to an existing 0x file.

if (args.Length is not (2 or 3))
{
    Console.Error.WriteLine("Usage: BlueTrack.Migrator <connectionString> <scriptsFolderPath> [skipScriptNames]");
    Environment.Exit(1);
    return;
}

var connectionString = args[0];
var scriptsFolderPath = args[1];
var skipScriptNames = args.Length == 3
    ? args[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase)
    : [];

if (!Directory.Exists(scriptsFolderPath))
{
    Console.Error.WriteLine($"Scripts folder not found: {scriptsFolderPath}");
    Environment.Exit(1);
    return;
}

var connectionStringBuilder = new SqlConnectionStringBuilder(connectionString);
var targetDatabaseName = connectionStringBuilder.InitialCatalog;

if (string.IsNullOrWhiteSpace(targetDatabaseName))
{
    Console.Error.WriteLine("Connection string must specify a target Database/Initial Catalog.");
    Environment.Exit(1);
    return;
}

if (!System.Text.RegularExpressions.Regex.IsMatch(targetDatabaseName, "^[A-Za-z_][A-Za-z0-9_]*$"))
{
    Console.Error.WriteLine($"Refusing to operate on database name '{targetDatabaseName}' -- expected a plain identifier (letters, digits, underscore, not starting with a digit) so it's safe to use directly in DDL.");
    Environment.Exit(1);
    return;
}

Console.WriteLine($"BlueTrack.Migrator: target database is '{targetDatabaseName}'.");

// Ensure the target database exists before DbUp ever tries to connect to
// it -- see the top-of-file comment. This only ever creates, never drops.
var masterConnectionStringBuilder = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" };
await using (var masterConnection = new SqlConnection(masterConnectionStringBuilder.ConnectionString))
{
    await masterConnection.OpenAsync();
    await using var command = masterConnection.CreateCommand();
    command.CommandText = $"IF DB_ID(N'{targetDatabaseName}') IS NULL CREATE DATABASE [{targetDatabaseName}];";
    await command.ExecuteNonQueryAsync();
}

var alwaysExcludedScriptNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "14_BlueTrack_ScheduleImportLoadJob.sql",
};

var upgrader = DeployChanges.To
    .SqlDatabase(connectionString)
    .WithScriptsFromFileSystem(scriptsFolderPath, path =>
        !Path.GetFileName(path).StartsWith("00_", StringComparison.OrdinalIgnoreCase)
        && !alwaysExcludedScriptNames.Contains(Path.GetFileName(path))
        && !skipScriptNames.Contains(Path.GetFileName(path)))
    .WithVariable("DatabaseName", targetDatabaseName)
    .LogToConsole()
    .Build();

var result = upgrader.PerformUpgrade();

if (!result.Successful)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine(result.Error);
    Console.ResetColor();
    Environment.Exit(1);
    return;
}

Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine("BlueTrack database upgrade successful.");
Console.ResetColor();
