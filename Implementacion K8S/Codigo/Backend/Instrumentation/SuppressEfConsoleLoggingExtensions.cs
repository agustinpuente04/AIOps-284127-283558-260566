using Microsoft.Extensions.Logging;

namespace Instrumentation;

public static class SuppressEfConsoleLoggingExtensions
{
    /// <summary>
    /// Silencia categorías de EF Core y SqlClient en el pipeline de logging (además de appsettings).
    /// </summary>
    public static ILoggingBuilder SuppressEfAndSqlClientLogs(this ILoggingBuilder logging)
    {
        logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.None);
        logging.AddFilter("Microsoft.Data.SqlClient", LogLevel.None);
        return logging;
    }
}
