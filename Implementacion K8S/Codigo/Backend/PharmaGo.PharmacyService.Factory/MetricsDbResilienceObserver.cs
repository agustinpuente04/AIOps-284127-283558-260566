using Instrumentation;
using Microsoft.Extensions.Logging;
using PharmaGo.DataAccess.Resilience;

namespace PharmaGo.PharmacyService.Factory;

/// <summary>Reenvía los eventos de resiliencia de la BD a las métricas (el gauge pharmago_circuit_breaker_state{target="db:Drug"}
/// alimenta la alerta existente "Circuit breaker abierto") y registra los fallos de BD con su tipo y número de error.</summary>
public sealed class MetricsDbResilienceObserver : IDbResilienceObserver
{
    private readonly ILogger _logger;

    public MetricsDbResilienceObserver(ILoggerFactory loggerFactory) => _logger = loggerFactory.CreateLogger("PharmaGo.DbResilience");

    public void Register(string target, Func<double> stateProvider) => ResilienceMetrics.RegisterProvider(target, stateProvider);
    public void OnRetry(string target) => ResilienceMetrics.RecordDbRetry(target);

    public void OnBreak(string target)
    {
        ResilienceMetrics.RecordBreak(target);
        _logger.LogWarning("Circuit breaker de la BD ABIERTO para {Target}", target);
    }

    public void OnHalfOpen(string target)
    {
        ResilienceMetrics.RecordHalfOpen(target);
        _logger.LogInformation("Circuit breaker de la BD half-open para {Target}: se deja pasar una consulta de prueba", target);
    }

    public void OnReset(string target)
    {
        ResilienceMetrics.RecordReset(target);
        _logger.LogInformation("Circuit breaker de la BD CERRADO para {Target}", target);
    }

    public void OnFailure(string target, Exception exception, bool handled, bool retryable)
    {
        // Se registra el primer SqlException de la cadena: su número de error es lo que permite ampliar la clasificación
        var sql = exception;
        while (sql != null && sql.GetType().Name != "SqlException") sql = sql.InnerException;
        var number = sql?.GetType().GetProperty("Number")?.GetValue(sql);
        if (handled)
            _logger.LogInformation("Fallo de BD en {Target}: {ExceptionType} (SQL {SqlNumber}), reintentable={Retryable}",
                target, (sql ?? exception).GetType().Name, number, retryable);
        else
            _logger.LogWarning("Fallo de BD NO clasificado en {Target}: {ExceptionType} (SQL {SqlNumber}): {Message}",
                target, (sql ?? exception).GetType().Name, number, (sql ?? exception).Message);
    }
}
