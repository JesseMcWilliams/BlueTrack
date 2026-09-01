using DbUp;

// BlueTrack.Migrator: runs Database/*.sql against BlueTrack via DbUp (D-58, D-67).
//
// Usage:
//   BlueTrack.Migrator <connectionString> <scriptsFolderPath>
//
// Both are required arguments rather than baked-in config -- this is a
// small deploy-time utility, not a long-running service, so there's no
// appsettings.json here (consistent with keeping dependencies minimal).
//
// IMPORTANT -- before running this against an environment that already has
// 01 through the current highest-numbered script applied by hand (as this
// project's Dev database does), run Database/10_BlueTrack_SeedDbUpJournal.sql
// first. That creates DbUp's own journal table (dbo.SchemaVersions) and
// marks the already-applied scripts as done, so this tool only runs
// genuinely new scripts -- never re-running 01's destructive DROP DATABASE
// against a live environment. See that script's header comment and D-58.

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: BlueTrack.Migrator <connectionString> <scriptsFolderPath>");
    Environment.Exit(1);
    return;
}

var connectionString = args[0];
var scriptsFolderPath = args[1];

if (!Directory.Exists(scriptsFolderPath))
{
    Console.Error.WriteLine($"Scripts folder not found: {scriptsFolderPath}");
    Environment.Exit(1);
    return;
}

var upgrader = DeployChanges.To
    .SqlDatabase(connectionString)
    .WithScriptsFromFileSystem(scriptsFolderPath)
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
