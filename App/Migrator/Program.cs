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
// Only the top level of <scriptsFolderPath> is read. The scripts that must
// never run through DbUp live in Database/Manual (D-195): database creation
// (it has to connect to `master`), the two SQL Agent job scripts and the
// backup-status grant (they `USE msdb;` and never switch back, so DbUp's
// journal write after them would fail against msdb -- confirmed by a real
// failed run, 2026-09-05), and the app-service-account and DevFakeAuth
// grants (DBA- or developer-run, with placeholders to fill in first).
//
// The target database name is parsed out of <connectionString>'s own
// Database/Initial Catalog and is the ONLY source of truth for which
// database the scripts touch -- it's passed into every script as DbUp's
// $DatabaseName$ substitution variable (see every script's
// `USE $DatabaseName$;`). Previously the scripts hardcoded the literal
// database name "BlueTrack", independent of whatever database
// <connectionString> actually pointed at -- a real incident (2026-09-03)
// ran this tool with a connection string pointed at BlueTrackTest and it
// silently dropped and recreated the real BlueTrack database instead.
// That class of mismatch is now structurally impossible: there is exactly
// one place the database name comes from.
//
// Before DbUp runs anything, this tool connects to `master` on the same
// server/credentials and ensures the target database exists (CREATE
// DATABASE if missing -- never DROP). That's the only thing that ever runs
// against `master`; DbUp itself then connects directly to the target
// database for its entire journal-tracked run, so dbo.SchemaVersions lives
// inside whichever database was actually built. A caller that wants a
// genuinely fresh database (CI's disposable BlueTrackTest, per
// Design_Testing-Strategy.md) drops it explicitly, as its own visible step,
// before invoking this tool.
//
// THE BASELINE (D-195, 2026-10-09). Database/01-06_BlueTrack_Baseline_*.sql
// replaced the numbered scripts 01-51 before the first release; they build
// the same schema and seed data from an empty database (proven with
// Deploy/Compare-BlueTrackSchema.ps1). DbUp's journal records scripts by
// file name only, so on a database built from the old scripts it would see
// the baseline as new and run it -- and its table scripts would destroy the
// data. Before upgrading, this tool therefore checks the journal: if it
// shows the old scripts through the last one (51), the baseline scripts are
// recorded as applied without running them. A database built from the old
// scripts but not up to 51 is refused: upgrade it first with the commit
// tagged `pre-sql-baseline`, whose Migrator still has the old scripts.
// Scripts after the baseline are ordinary small, guarded, numbered scripts
// (Database/README.md, "Adding a script").

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

// D-195: record the baseline as applied on a database built from the old
// scripts (see the top-of-file comment). Only when this run's folder holds
// the baseline -- never for Database/Test.
string[] baselineScriptNames =
[
    "01_BlueTrack_Baseline_CoreSchema.sql",
    "02_BlueTrack_Baseline_EtlLoads.sql",
    "03_BlueTrack_Baseline_SourceImport.sql",
    "04_BlueTrack_Baseline_WebSchema.sql",
    "05_BlueTrack_Baseline_WebSeed.sql",
    "06_BlueTrack_Baseline_WebLogic.sql",
];
const string LastPreBaselineScriptName = "51_BlueTrack_TargetsFromCyberArkAddress.sql";

if (baselineScriptNames.Any(name => File.Exists(Path.Combine(scriptsFolderPath, name))))
{
    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync();

    var journal = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    await using (var read = connection.CreateCommand())
    {
        // EXEC, so the batch still compiles when the journal doesn't exist yet.
        read.CommandText = "IF OBJECT_ID(N'dbo.SchemaVersions', N'U') IS NOT NULL EXEC(N'SELECT ScriptName FROM dbo.SchemaVersions');";
        await using var reader = await read.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            journal.Add(reader.GetString(0));
        }
    }

    // Database/Test scripts share the journal; they say nothing about the schema.
    var schemaScriptsRun = journal.Where(name => !name.Contains("_Test_", StringComparison.OrdinalIgnoreCase)).ToList();

    if (schemaScriptsRun.Count > 0 && !baselineScriptNames.Any(journal.Contains))
    {
        if (!journal.Contains(LastPreBaselineScriptName))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine(
                $"'{targetDatabaseName}' was built from the numbered scripts that the baseline replaced (D-195), but it is not " +
                $"up to {LastPreBaselineScriptName}. Upgrade it first with the commit tagged 'pre-sql-baseline' (its App/Migrator " +
                "still has those scripts), then run this again. Nothing was changed.");
            Console.ResetColor();
            Environment.Exit(1);
            return;
        }

        await using var mark = connection.CreateCommand();
        mark.CommandText = "INSERT INTO dbo.SchemaVersions (ScriptName, Applied) VALUES (@ScriptName, GETDATE());";
        var scriptName = mark.Parameters.Add("@ScriptName", System.Data.SqlDbType.NVarChar, 255);
        foreach (var name in baselineScriptNames)
        {
            scriptName.Value = name;
            await mark.ExecuteNonQueryAsync();
        }
        Console.WriteLine($"Baseline (D-195): '{targetDatabaseName}' already has this schema from the old scripts (through " +
                          $"{LastPreBaselineScriptName}); recorded the {baselineScriptNames.Length} baseline scripts as applied without running them.");
    }
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
