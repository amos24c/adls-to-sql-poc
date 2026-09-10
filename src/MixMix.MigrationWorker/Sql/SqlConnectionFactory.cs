using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using MixMix.MigrationWorker.Configuration;

namespace MixMix.MigrationWorker.Sql;

public interface ISqlConnectionFactory
{
    Task<SqlConnection> OpenAsync(CancellationToken cancellationToken);
}

public sealed class SqlConnectionFactory : ISqlConnectionFactory
{
    public const string ConnectionStringName = "TargetSql";

    /// <summary>Marker left in appsettings.json so the real password never lives in source control.</summary>
    private const string SecretPlaceholder = "__SET_VIA_USER_SECRETS__";

    private readonly string _connectionString;

    public SqlConnectionFactory(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new MigrationConfigurationException(
                $"ConnectionStrings:{ConnectionStringName} must be set.");
        }

        if (connectionString.Contains(SecretPlaceholder, StringComparison.Ordinal))
        {
            throw new MigrationConfigurationException(
                $"ConnectionStrings:{ConnectionStringName} still contains the {SecretPlaceholder} placeholder. "
                + "Supply the real connection string with "
                + "`dotnet user-secrets set \"ConnectionStrings:TargetSql\" \"...\"` "
                + "or the ConnectionStrings__TargetSql environment variable.");
        }

        _connectionString = connectionString;
    }

    public async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_connectionString);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
