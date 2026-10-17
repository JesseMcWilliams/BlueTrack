using System.Data;
using BlueTrack.Api.Data;
using Microsoft.Data.SqlClient;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// Layer 2 (Design_Testing-Strategy.md): real SQL Server, real Dapper
/// repositories, no HTTP layer. Points at the disposable BlueTrackTest
/// database -- built by App/Migrator against Database/ (skipping 09) plus
/// Database/Test/01_BlueTrack_Test_DevFakeAuthMatrixSeed.sql, never a copy
/// of real data.
///
/// The connection string is read from the BLUETRACK_TEST_CONNECTION
/// environment variable so CI can point it anywhere; it falls back to this
/// dev host's own BlueTrackTest_Local (D-240) for running these tests
/// locally. CI owns BlueTrackTest and rebuilds it on every run, so local
/// runs don't share it. Build the local one with
/// Tools/Reset-BlueTrackLocalTestDb.ps1.
/// </summary>
public static class TestDatabase
{
    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("BLUETRACK_TEST_CONNECTION")
        ?? "Server=WIN-K5POLANERI5.Company.com;Database=BlueTrackTest_Local;Integrated Security=true;TrustServerCertificate=true";
}

public sealed class TestDbConnectionFactory : IDbConnectionFactory
{
    public IDbConnection Create() => new SqlConnection(TestDatabase.ConnectionString);
}
