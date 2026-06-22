using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FocusMed.Data.Interceptors;

public class SqliteWalInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        base.ConnectionOpened(connection, eventData);
        EnableWal(connection);
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
        EnableWal(connection);
    }

    private static void EnableWal(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
        command.ExecuteNonQuery();
    }
}
