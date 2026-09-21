using System.Reflection;

namespace BlueTrack.Api.Auth;

/// <summary>
/// Substitutes a resolved account name into the embedded copy of
/// Database/32_BlueTrack_GrantBackupStatusReaderRole.sql, for
/// GroupRoleMappingsController's script-generator endpoint. Mirrors
/// Deploy/Modules/BlueTrack.Database.psm1's Install-BlueTrackNightlyJob:
/// a plain string replacement against a known placeholder, guarded so
/// drift in the tracked script (e.g. the placeholder text changing)
/// throws loudly instead of silently generating a broken script.
/// </summary>
public static class BackupStatusReaderScriptGenerator
{
    private const string TargetAccountPlaceholder = "__TARGET_ACCOUNT__";

    public static string Generate(string targetAccountName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("BlueTrack.Api.GrantBackupStatusReaderRole.sql")
            ?? throw new InvalidOperationException("Embedded resource 'BlueTrack.Api.GrantBackupStatusReaderRole.sql' not found -- check BlueTrack.Api.csproj's EmbeddedResource entry.");
        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();

        if (!content.Contains(TargetAccountPlaceholder, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Did not find the expected '{TargetAccountPlaceholder}' placeholder in 32_BlueTrack_GrantBackupStatusReaderRole.sql -- the script may have changed upstream. Update this generator before continuing.");
        }

        return content.Replace(TargetAccountPlaceholder, targetAccountName, StringComparison.Ordinal);
    }
}
