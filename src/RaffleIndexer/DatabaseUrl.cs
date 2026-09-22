using Npgsql;

namespace RaffleIndexer;

/// <summary>
/// Translates a platform-supplied database URL into a connection string Npgsql
/// understands.
/// <para>
/// Render, Railway and Heroku all expose Postgres as
/// <c>postgresql://user:password@host:port/database</c>. Npgsql only parses
/// key-value form, so without this the app fails to connect the moment it leaves
/// a local Compose setup — where the connection string is already key-value and
/// passes through untouched.
/// </para>
/// </summary>
public static class DatabaseUrl
{
    public static string Normalize(string? connectionStringOrUrl)
    {
        if (string.IsNullOrWhiteSpace(connectionStringOrUrl)) return connectionStringOrUrl ?? "";

        var value = connectionStringOrUrl.Trim();
        if (!value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return connectionStringOrUrl;
        }

        var uri = new Uri(value);
        var credentials = uri.UserInfo.Split(':', 2);

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? 5432 : uri.Port,
            Database = uri.AbsolutePath.TrimStart('/'),
            Username = Uri.UnescapeDataString(credentials[0]),
            Password = credentials.Length > 1 ? Uri.UnescapeDataString(credentials[1]) : "",

            // Managed Postgres requires TLS. Require encrypts without demanding a
            // verifiable chain, which suits a provider-issued certificate;
            // VerifyCA or VerifyFull would be the stricter options.
            SslMode = SslMode.Require
        };

        return builder.ConnectionString;
    }
}
