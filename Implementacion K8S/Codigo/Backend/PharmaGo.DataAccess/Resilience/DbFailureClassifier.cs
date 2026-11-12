using Microsoft.Data.SqlClient;

namespace PharmaGo.DataAccess.Resilience;

/// <summary>Decide cuándo un fallo de la BD debe contar para el circuit breaker y cuándo vale la pena reintentarlo.</summary>
public interface IDbFailureClassifier
{
    /// <summary>
    /// ¿Es un fallo de disponibilidad de la BD? (timeout, conexión rota o imposible, pool agotado). Cuenta para el breaker y se
    /// traduce a <see cref="PharmaGo.Exceptions.DatabaseUnavailableException"/>. Los errores de negocio, de constraint o de
    /// sintaxis NO lo son: fallarían igual al reintentar y no indican un problema de la BD.
    /// </summary>
    bool IsHandled(Exception exception);

    /// <summary>
    /// ¿Merece un reintento inmediato? Solo los fallos transitorios de CONEXIÓN. Un timeout de comando o un pool agotado no:
    /// reintentar contra una tabla bloqueada multiplica la espera y la carga justo cuando más daño hace.
    /// </summary>
    bool IsRetryable(Exception exception);
}

/// <summary>Clasificador para SQL Server / Microsoft.Data.SqlClient. Recorre las excepciones internas (EF envuelve en DbUpdateException).</summary>
public class SqlDbFailureClassifier : IDbFailureClassifier
{
    // Timeout de comando: SqlClient lo informa con Number = -2
    private const int TimeoutNumber = -2;

    // 258: "wait operation timed out" al conectar. Cuenta como fallo de la BD pero NO se reintenta (como cualquier timeout).
    private const int ConnectWaitTimeoutNumber = 258;

    // Fallos transitorios de conexión (lista de Microsoft para SQL Server y Azure SQL, más los que aparecen al reiniciar un
    // SQL Server on-premise/contenedor: login durante el arranque, pipe cerrado, conexión reseteada, deadlock víctima).
    private static readonly HashSet<int> TransientNumbers = new()
    {
        -1,                              // "network-related or instance-specific error": no se pudo establecer la conexión
        20, 64, 233, 53, 121,            // conexión no inicializada, pipe cerrado, semáforo
        10061, 11001,                    // conexión rechazada (BD reiniciándose o sin endpoints), host no resuelto
        1205,                            // víctima de deadlock (una lectura puede repetirse sin riesgo)
        4060, 18401,                     // base no disponible / servidor arrancando
        10053, 10054, 10060,             // transporte: conexión abortada, reseteada, timeout de conexión
        10928, 10929,                    // límites de recursos (Azure)
        40197, 40501, 40613,             // servicio ocupado / reconfigurando
        49918, 49919, 49920
    };

    public virtual bool IsHandled(Exception exception)
    {
        foreach (var e in Chain(exception))
        {
            if (e is TimeoutException) return true;
            if (TryGetSqlNumber(e, out var number) &&
                (number == TimeoutNumber || number == ConnectWaitTimeoutNumber || TransientNumbers.Contains(number))) return true;
            if (IsTransportError(e) || IsPoolExhausted(e) || IsConnectionFailureMessage(e)) return true;
        }
        return false;
    }

    public virtual bool IsRetryable(Exception exception)
    {
        foreach (var e in Chain(exception))
        {
            if (e is TimeoutException) return false;
            if (TryGetSqlNumber(e, out var number))
            {
                if (number == TimeoutNumber || number == ConnectWaitTimeoutNumber) return false;
                if (TransientNumbers.Contains(number)) return true;
            }
            if (IsPoolExhausted(e)) return false;
            if (IsTransportError(e)) return true;
        }
        return false;
    }

    /// <summary>Punto de extensión para pruebas: SqlException no se puede construir fuera de SqlClient.</summary>
    protected virtual bool TryGetSqlNumber(Exception exception, out int number)
    {
        if (exception is SqlException sql)
        {
            number = sql.Number;
            return true;
        }
        number = 0;
        return false;
    }

    // "A transport-level error has occurred when sending the request to the server": conexión vieja del pool tras reiniciar la BD
    private static bool IsTransportError(Exception e) =>
        e is SqlException or InvalidOperationException or IOException &&
        e.Message.Contains("transport-level error", StringComparison.OrdinalIgnoreCase);

    // Mensaje canónico de SqlClient cuando no se pudo establecer la conexión ("A network-related or instance-specific error occurred
    // while establishing a connection to SQL Server... error: 40 - Could not open a connection"). Sirve de respaldo por si el número de
    // error de esa condición varía entre plataformas o versiones. Cuenta para el breaker pero NO se reintenta: el mismo mensaje cubre
    // tanto "rechazada" (inmediato) como una espera de conexión agotada (5 s), y reintentar esta última multiplica la espera.
    private static bool IsConnectionFailureMessage(Exception e) =>
        e.Message.Contains("network-related or instance-specific error", StringComparison.OrdinalIgnoreCase) ||
        e.Message.Contains("Could not open a connection to SQL Server", StringComparison.OrdinalIgnoreCase) ||
        e.Message.Contains("server was not found or was not accessible", StringComparison.OrdinalIgnoreCase);

    // "Timeout expired. The timeout period elapsed prior to obtaining a connection from the pool..."
    private static bool IsPoolExhausted(Exception e) =>
        e is InvalidOperationException &&
        (e.Message.Contains("obtaining a connection from the pool", StringComparison.OrdinalIgnoreCase) ||
         e.Message.Contains("max pool size", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        for (var e = exception; e != null; e = e.InnerException) yield return e;
    }
}
