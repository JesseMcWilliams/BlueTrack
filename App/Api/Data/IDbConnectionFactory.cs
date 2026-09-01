using System.Data;

namespace BlueTrack.Api.Data;

/// <summary>
/// Every call opens a fresh connection using Windows Integrated Authentication
/// under the app pool identity (D-30) -- no SQL login, no standing secret.
/// </summary>
public interface IDbConnectionFactory
{
    IDbConnection Create();
}
