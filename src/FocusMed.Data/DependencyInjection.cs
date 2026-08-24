using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace FocusMed.Data;

/// <summary>
/// Enables WAL journaling, busy timeout, and NORMAL sync on every SQLite connection.
/// Without WAL, concurrent writers (Worker ingestion, Dashboard, PrintCapture) collide
/// with "database is locked" errors after the 30s command timeout.
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    private const string Pragmas =
        "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        if (connection is SqliteConnection sqlite)
        {
            using var cmd = sqlite.CreateCommand();
            cmd.CommandText = Pragmas;
            cmd.ExecuteNonQuery();
        }
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (connection is SqliteConnection sqlite)
        {
            await using var cmd = sqlite.CreateCommand();
            cmd.CommandText = Pragmas;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}

public static class DependencyInjection
{
    public static IServiceCollection AddFocusMedData(this IServiceCollection services, string connectionString)
    {
        var interceptor = new SqlitePragmaInterceptor();

        services.AddDbContextFactory<FocusMedDbContext>((_, options) =>
        {
            options.UseSqlite(connectionString);
            options.AddInterceptors(interceptor);
        });

        return services;
    }
}
