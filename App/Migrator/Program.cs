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
// filenames (matched exactly, e.g. "09_BlueTrack_ScheduleImportLoadJob.sql")
// to exclude from this run. Needed for 09 specifically -- it's SQL Agent
// job scheduling, not app schema/data, switches context with its own
// `USE msdb;`, and always targets the real BlueTrack database and real
// file paths by design regardless of <connectionString> -- none of which
// belongs in a disposable database build (CI's BlueTrackTest, per
// Design_Testing_Strategy.md). Its own `USE msdb;` also breaks DbUp's
// per-database journal tracking for anything run after it in the same
// pass, which is how this was actually found (2026-09-03).
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
// a database -- 01_BlueTrack_CreateDatabase_Schema.sql no longer does
// either (its old DROP/CREATE DATABASE preamble was removed for exactly
// this reason: SQL Server can't drop a database a connection is currently
// using, which is what the 2026-09-03 incident's fix required). A caller
// that wants a genuinely fresh database (CI's disposable BlueTrackTest,
// per Design_Testing_Strategy.md) drops it explicitly, as its own visible
// step, before invoking this tool -- not something this shared tool does
// silently on every run, since most callers (real Dev/Staging/Prod
// environments) must never have their database dropped.
//
// IMPORTANT -- before running this against an environment that already has
// 01 through the current highest-numbered script applied by hand (as this
// project's Dev database does), run Database/10_BlueTrack_SeedDbUpJournal.sql
// first. That creates DbUp's own journal table (dbo.SchemaVersions) and
// marks the already-applied scripts as done, so this tool only runs
// genuinely new scripts. 01 no longer drops the database, but it still
// unconditionally drops and recreates every table it defines -- re-running
// it against an environment that already holds real data is still
// destructive to that data. See that script's header comment and D-58.

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

var upgrader = DeployChanges.To
    .SqlDatabase(connectionString)
    .WithScriptsFromFileSystem(scriptsFolderPath, path => !skipScriptNames.Contains(Path.GetFileName(path)))
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
