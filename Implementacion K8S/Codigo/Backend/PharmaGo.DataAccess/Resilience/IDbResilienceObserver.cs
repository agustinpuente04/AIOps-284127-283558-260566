namespace PharmaGo.DataAccess.Resilience;

/// <summary>
/// Notificaciones de las políticas de resiliencia de la BD. DataAccess no depende de la librería de métricas: cada servicio
/// implementa esta interfaz y la reenvía a <c>ResilienceMetrics</c> (gauge <c>pharmago_circuit_breaker_state{target="db:Drug"}</c>
/// y counters de reintentos y aperturas), con lo que la alerta existente "Circuit breaker abierto" cubre también la BD.
/// </summary>
public interface IDbResilienceObserver
{
    /// <summary>
    /// <paramref name="stateProvider"/> devuelve el estado REAL del breaker en cada medición (0 cerrado, 0.5 half-open, 1 abierto).
    /// Un gauge actualizado solo en las transiciones se queda "abierto" para siempre si la entidad deja de recibir tráfico:
    /// un breaker solo vuelve a cerrarse con una llamada exitosa.
    /// </summary>
    void Register(string target, Func<double> stateProvider);
    void OnRetry(string target);
    void OnBreak(string target);
    void OnHalfOpen(string target);
    void OnReset(string target);

    /// <summary>Un fallo de BD llegó a la capa de resiliencia (para registrarlo: tipo, número de error y mensaje).</summary>
    void OnFailure(string target, Exception exception, bool handled, bool retryable);
}

public sealed class NullDbResilienceObserver : IDbResilienceObserver
{
    public void Register(string target, Func<double> stateProvider) { }
    public void OnRetry(string target) { }
    public void OnBreak(string target) { }
    public void OnHalfOpen(string target) { }
    public void OnReset(string target) { }
    public void OnFailure(string target, Exception exception, bool handled, bool retryable) { }
}
