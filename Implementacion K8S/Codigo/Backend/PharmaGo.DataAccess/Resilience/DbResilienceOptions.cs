using Microsoft.Data.SqlClient;

namespace PharmaGo.DataAccess.Resilience;

/// <summary>
/// Parámetros de resiliencia del acceso a la base de datos. Se leen de la configuración (<c>Database:Resilience:*</c>,
/// variables de entorno con <c>__</c>) sin depender de los paquetes de configuración, para que DataAccess siga siendo liviano.
/// </summary>
public class DbResilienceOptions
{
    /// <summary>
    /// Timeout de cada comando SQL. Es la palanca que hace fallar rápido: las consultas de EF son síncronas, así que solo
    /// SqlClient puede cortarlas (un timeout de Polly no interrumpe una llamada bloqueante). El valor por defecto de
    /// SqlClient (30 s) dejaba un hilo y una conexión ocupados medio minuto por request bloqueada (escenario C4).
    /// </summary>
    public int CommandTimeoutSeconds { get; set; } = 5;

    /// <summary>Timeout para abrir una conexión (por defecto de SqlClient: 15 s). Solo se aplica si el connection string no lo define.</summary>
    public int ConnectTimeoutSeconds { get; set; } = 5;

    /// <summary>Timeout de las migraciones de EF al arrancar: no deben heredar el timeout corto.</summary>
    public int MigrationTimeoutSeconds { get; set; } = 300;

    /// <summary>Fallos manejados CONSECUTIVOS de una entidad antes de abrir su breaker.</summary>
    public int BreakerFailures { get; set; } = 5;

    /// <summary>Cuánto permanece abierto el breaker antes de dejar pasar una request de prueba (half-open).</summary>
    public int BreakDurationSeconds { get; set; } = 15;

    /// <summary>Reintentos (además del intento original) para lecturas con fallos transitorios de conexión.</summary>
    public int RetryCount { get; set; } = 3;
    public int RetryBaseDelayMs { get; set; } = 200;
    public int RetryMaxDelayMs { get; set; } = 2000;

    public TimeSpan CommandTimeout => TimeSpan.FromSeconds(CommandTimeoutSeconds);
    public TimeSpan BreakDuration => TimeSpan.FromSeconds(BreakDurationSeconds);
    public TimeSpan RetryBaseDelay => TimeSpan.FromMilliseconds(RetryBaseDelayMs);
    public TimeSpan RetryMaxDelay => TimeSpan.FromMilliseconds(RetryMaxDelayMs);

    /// <summary>Lee las opciones con una función de búsqueda (p. ej. <c>key => configuration[key]</c>); lo no definido queda por defecto.</summary>
    public static DbResilienceOptions FromConfiguration(Func<string, string?> lookup)
    {
        var options = new DbResilienceOptions();
        int Read(string name, int fallback) =>
            int.TryParse(lookup("Database:Resilience:" + name), out var value) && value > 0 ? value : fallback;

        options.CommandTimeoutSeconds = Read(nameof(CommandTimeoutSeconds), options.CommandTimeoutSeconds);
        options.ConnectTimeoutSeconds = Read(nameof(ConnectTimeoutSeconds), options.ConnectTimeoutSeconds);
        options.MigrationTimeoutSeconds = Read(nameof(MigrationTimeoutSeconds), options.MigrationTimeoutSeconds);
        options.BreakerFailures = Read(nameof(BreakerFailures), options.BreakerFailures);
        options.BreakDurationSeconds = Read(nameof(BreakDurationSeconds), options.BreakDurationSeconds);
        options.RetryCount = Read(nameof(RetryCount), options.RetryCount);
        options.RetryBaseDelayMs = Read(nameof(RetryBaseDelayMs), options.RetryBaseDelayMs);
        options.RetryMaxDelayMs = Read(nameof(RetryMaxDelayMs), options.RetryMaxDelayMs);
        return options;
    }

    /// <summary>
    /// Devuelve el connection string con un timeout de conexión corto, salvo que ya traiga uno explícito.
    /// Nunca lanza: si el string no se puede interpretar se devuelve tal cual y falla donde siempre falló.
    /// </summary>
    public string ApplyConnectTimeout(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return connectionString ?? "";
        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            var explicitTimeout = connectionString.Contains("Timeout", StringComparison.OrdinalIgnoreCase);
            if (!explicitTimeout) builder.ConnectTimeout = ConnectTimeoutSeconds;
            return builder.ConnectionString;
        }
        catch (ArgumentException)
        {
            return connectionString;
        }
    }
}
