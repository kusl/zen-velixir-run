using Npgsql;

namespace Zen.Blazor.Beacons;

public static class PostgresSettings
{
    public const string Host = "postgreshost";
    public const string Port = "postgresport";
    public const string Database = "postgresdatabase";
    public const string User = "postgresuser";
    public const string Password = "postgrespassword";
    public const string Url = "postgresconnectionstring";

    public static string? ConnectionString(IConfiguration configuration, int maxPoolSize)
    {
        var builder = new NpgsqlConnectionStringBuilder();

        if (!string.IsNullOrWhiteSpace(configuration[Host]))
        {
            builder.Host = configuration[Host];
            if (int.TryParse(configuration[Port], out var port))
            {
                builder.Port = port;
            }

            builder.Database = configuration[Database];
            builder.Username = configuration[User];
            builder.Password = configuration[Password];
        }
        else if (Uri.TryCreate(configuration[Url], UriKind.Absolute, out var uri) && uri.Scheme is "postgres" or "postgresql")
        {
            builder.Host = uri.Host;
            if (uri.Port > 0)
            {
                builder.Port = uri.Port;
            }

            builder.Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
            var credentials = uri.UserInfo.Split(':', 2);
            builder.Username = Uri.UnescapeDataString(credentials[0]);
            if (credentials.Length > 1)
            {
                builder.Password = Uri.UnescapeDataString(credentials[1]);
            }
        }
        else
        {
            return null;
        }

        builder.Pooling = true;
        builder.MinPoolSize = 0;
        builder.MaxPoolSize = maxPoolSize;
        builder.ConnectionIdleLifetime = 60;
        builder.ConnectionPruningInterval = 10;
        builder.Timeout = 5;
        builder.CommandTimeout = 15;
        builder.KeepAlive = 30;
        builder.SslMode = SslMode.Prefer;
        builder.ApplicationName = "zen";
        return builder.ConnectionString;
    }
}
