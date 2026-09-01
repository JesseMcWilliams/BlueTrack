using System.Data;
using Microsoft.Data.SqlClient;

namespace BlueTrack.Api.Data;

/// <summary>
/// Reads ConnectionStrings:BlueTrackDb from appsettings.json -- Windows
/// Integrated Authentication (D-30), so no username/password ever appears
/// there. The app pool identity itself (a dedicated low-privilege domain
/// service account or gMSA, not the default ApplicationPoolIdentity) needs
/// a SQL Server login granted before this connects successfully.
/// </summary>
public sealed class SqlConnectionFactory(IConfiguration configuration) : IDbConnectionFactory
{
    private readonly string _connectionString =
        configuration.GetConnectionString("BlueTrackDb")
        ?? throw new InvalidOperationException("Missing ConnectionStrings:BlueTrackDb in configuration.");

    public IDbConnection Create() => new SqlConnection(_connectionString);
}
